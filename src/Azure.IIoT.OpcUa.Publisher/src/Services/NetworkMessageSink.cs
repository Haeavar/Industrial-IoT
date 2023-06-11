﻿// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace Azure.IIoT.OpcUa.Publisher.Services
{
    using Azure.IIoT.OpcUa.Publisher;
    using Azure.IIoT.OpcUa.Publisher.Models;
    using Azure.IIoT.OpcUa.Publisher.Stack;
    using Azure.IIoT.OpcUa.Publisher.Stack.Models;
    using Furly.Extensions.Messaging;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Diagnostics.Metrics;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Threading.Tasks.Dataflow;

    /// <summary>
    /// Network message sink connected to the source. The sink
    /// is really a dataflow engine to handle batching and
    /// encoding and other egress concerns.
    /// </summary>
    public sealed class NetworkMessageSink : IWriterGroup
    {
        /// <inheritdoc/>
        public IMessageSource Source { get; }

        /// <summary>
        /// Create engine
        /// </summary>
        /// <param name="writerGroup"></param>
        /// <param name="eventClients"></param>
        /// <param name="source"></param>
        /// <param name="encoder"></param>
        /// <param name="options"></param>
        /// <param name="logger"></param>
        /// <param name="metrics"></param>
        /// <param name="diagnostics"></param>
        public NetworkMessageSink(WriterGroupModel writerGroup,
            IEnumerable<IEventClient> eventClients, IMessageSource source,
            IMessageEncoder encoder, IOptions<PublisherOptions> options,
            ILogger<NetworkMessageSink> logger, IMetricsContext metrics,
            IWriterGroupDiagnostics? diagnostics = null)
        {
            Source = source;

            _metrics = metrics
                ?? throw new ArgumentNullException(nameof(metrics));
            _options = options;

            // Reverse the registration to have highest prio first.
            var registered = eventClients?.Reverse().ToList()
                ?? throw new ArgumentNullException(nameof(eventClients));
            if (registered.Count == 0)
            {
                throw new ArgumentException("No transports registered.",
                    nameof(eventClients));
            }
            _eventClient =
                   registered.Find(e => e.Name.Equals(
                    writerGroup.Transport?.ToString(),
                        StringComparison.OrdinalIgnoreCase))
                ?? registered.Find(e => e.Name.Equals(
                    options.Value.DefaultTransport?.ToString(),
                        StringComparison.OrdinalIgnoreCase))
                ?? registered[0];
            _messageEncoder = encoder;
            _logger = logger;
            _diagnostics = diagnostics;
            _cts = new CancellationTokenSource();

            _logNotifications = _options.Value.DebugLogNotifications
                ?? false;
            _maxNotificationsPerMessage = (int?)writerGroup.NotificationPublishThreshold
                ?? _options.Value.BatchSize ?? 0;
            _maxNetworkMessageSize = (int?)writerGroup.MaxNetworkMessageSize
                ?? _options.Value.MaxNetworkMessageSize ?? 0;

            if (_maxNetworkMessageSize <= 0)
            {
                _maxNetworkMessageSize = int.MaxValue;
            }
            if (_maxNetworkMessageSize > _eventClient.MaxEventPayloadSizeInBytes)
            {
                _maxNetworkMessageSize = _eventClient.MaxEventPayloadSizeInBytes;
            }

            _batchTriggerInterval = writerGroup.PublishingInterval
                ?? _options.Value.BatchTriggerInterval ?? TimeSpan.Zero;
            _maxPublishQueueSize = (int?)writerGroup.PublishQueueSize
                ?? _options.Value.MaxNetworkMessageSendQueueSize ?? kMaxQueueSize;

            //
            // set notification buffer to 1 if no publishing interval otherwise queue
            // as much as reasonable
            //
            if (_maxNotificationsPerMessage < 1)
            {
                _maxNotificationsPerMessage = _batchTriggerInterval == TimeSpan.Zero ?
                    1 : _maxPublishQueueSize;
            }

            _batchTriggerIntervalTimer = new Timer(BatchTriggerIntervalTimer_Elapsed);
            _encodingBlock =
                new TransformManyBlock<IOpcUaSubscriptionNotification[], (IEvent, Action)>(
                    EncodeNotifications, new ExecutionDataflowBlockOptions());
            _batchDataSetMessageBlock = new BatchBlock<IOpcUaSubscriptionNotification>(
                _maxNotificationsPerMessage, new GroupingDataflowBlockOptions());
            _sinkBlock = new ActionBlock<(IEvent, Action)>(
                SendAsync, new ExecutionDataflowBlockOptions());

            _batchDataSetMessageBlock.LinkTo(_encodingBlock);
            _encodingBlock.LinkTo(_sinkBlock);

            Source.OnMessage += OnMessageReceived;
            Source.OnCounterReset += MessageTriggerCounterResetReceived;

            InitializeMetrics();
            _logger.LogInformation(
                "Writer group {WriterGroup} set up to publish messages to {Transport}...",
                writerGroup.Name, _eventClient.Name);
        }

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            try
            {
                _batchTriggerIntervalTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                Source.OnCounterReset -= MessageTriggerCounterResetReceived;
                Source.OnMessage -= OnMessageReceived;
                _batchDataSetMessageBlock.Complete();
                await _batchDataSetMessageBlock.Completion.ConfigureAwait(false);
                _encodingBlock.Complete();
                await _encodingBlock.Completion.ConfigureAwait(false);
                _sinkBlock.Complete();
                await _sinkBlock.Completion.ConfigureAwait(false);
                _batchTriggerIntervalTimer?.Dispose();
            }
            finally
            {
                _diagnostics?.Dispose();
                await Source.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            try
            {
                _cts.Cancel();
                _meter.Dispose();
            }
            finally
            {
                DisposeAsync().AsTask().GetAwaiter().GetResult();
                _cts.Dispose();
            }
        }

        /// <summary>
        /// Encode notifications
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        private IEnumerable<(IEvent, Action)> EncodeNotifications(IOpcUaSubscriptionNotification[] input)
        {
            try
            {
                return _messageEncoder.Encode(_eventClient.CreateEvent,
                    input, _maxNetworkMessageSize, _maxNotificationsPerMessage != 1);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Encoding failure.");
                input.ForEach(a => a.Dispose());
                return Enumerable.Empty<(IEvent, Action)>();
            }
        }

        /// <summary>
        /// Send message
        /// </summary>
        /// <param name="message"></param>
        /// <returns></returns>
        private async Task SendAsync((IEvent Event, Action Complete) message)
        {
            try
            {
                // Do not give up and try to send the message until cancelled.
                var sw = Stopwatch.StartNew();
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        // Throws if cancelled
                        await message.Event.SendAsync(_cts.Token).ConfigureAwait(false);
                        break;
                    }
                    catch (Exception e) when (
                        e is not OperationCanceledException &&
                        e is not ObjectDisposedException)
                    {
                        kMessagesErrors.Add(1, _metrics.TagList);

                        var delay = TimeSpan.FromMilliseconds(attempt * 100);
                        if (_logger.IsEnabled(LogLevel.Debug))
                        {
                            _logger.LogDebug(e, "#{Attempt}: Error during attempt sending " +
                                "network message. Retrying in {Delay}...", attempt, delay);
                        }
                        else
                        {
                            _logger.LogError("#{Attempt}: Error '{Error}' sending network " +
                                "message. Retrying in {Delay}...", attempt, e.Message, delay);
                        }

                        // Throws if cancelled
                        await Task.Delay(delay, _cts.Token).ConfigureAwait(false);
                    }
                }

                // Message successfully published.
                _messagesSentCount++;
                kSendingDuration.Record(sw.ElapsedMilliseconds, _metrics.TagList);
                message.Complete();
                message.Event.Dispose();
                return;
            }
            catch (ObjectDisposedException) { }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                _logger.LogError(e, "Unexpected error sending network message.");
            }
        }

        /// <summary>
        /// Batch trigger interval
        /// </summary>
        /// <param name="state"></param>
        private void BatchTriggerIntervalTimer_Elapsed(object? state)
        {
            if (_batchTriggerInterval > TimeSpan.Zero)
            {
                _batchTriggerIntervalTimer.Change(_batchTriggerInterval,
                    Timeout.InfiniteTimeSpan);
            }
            _batchDataSetMessageBlock?.TriggerBatch();
        }

        /// <summary>
        /// Message received handler
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private void OnMessageReceived(object? sender, IOpcUaSubscriptionNotification args)
        {
            if (_dataFlowStartTime == DateTime.MinValue)
            {
                if (_batchTriggerInterval > TimeSpan.Zero)
                {
                    _batchTriggerIntervalTimer.Change(_batchTriggerInterval, Timeout.InfiniteTimeSpan);
                }
                _diagnostics?.ResetWriterGroupDiagnostics();
                _dataFlowStartTime = DateTime.UtcNow;
                _logger.LogInformation("Started data flow with message from subscription {Name} on {Endpoint}.",
                    args.SubscriptionName, args.EndpointUrl);
            }

            if (_sinkBlock.InputCount >= _maxPublishQueueSize)
            {
                _sinkBlockInputDroppedCount++;

                if (_logNotifications)
                {
                    LogNotification(args, true);
                }

                // Dispose arg
                args.Dispose();
            }
            else
            {
                if (_logNotifications)
                {
                    LogNotification(args);
                }

                _batchDataSetMessageBlock.Post(args);
            }
        }

        /// <summary>
        /// Counter reset
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void MessageTriggerCounterResetReceived(object? sender, EventArgs e)
        {
            _dataFlowStartTime = DateTime.MinValue;
        }

        /// <summary>
        /// Log notifications for debugging
        /// </summary>
        /// <param name="args"></param>
        /// <param name="dropped"></param>
        private void LogNotification(IOpcUaSubscriptionNotification args, bool dropped = false)
        {
            _logger.LogInformation(
                "{Action}Notification#{Seq} from Subscription {Subscription}{Items}",
                dropped ? "!!!! Dropped " : string.Empty, args.SequenceNumber,
                args.SubscriptionName, Stringify(args.Notifications));
            static string Stringify(IList<MonitoredItemNotificationModel> notifications)
            {
                var sb = new StringBuilder();
                foreach (var item in notifications)
                {
                    sb
                        .AppendLine()
                        .Append("|#")
                        .Append(item.SequenceNumber)
                        .Append('|')
                        .Append(item.Value?.ServerTimestamp.ToString("hh.mm.ss.ffffff"))
                        .Append('|')
                        .Append(item.DataSetFieldName ?? item.DataSetName)
                        .Append('|')
                        .Append(item.Value?.SourceTimestamp.ToString("hh.mm.ss.ffffff"))
                        .Append('|')
                        .Append(item.Value?.Value)
                        .Append('|')
                        .Append(item.Value?.StatusCode)
                        .Append('|')
                        ;
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// Create observable metrics
        /// </summary>
        private void InitializeMetrics()
        {
            _meter.CreateObservableCounter("iiot_edge_publisher_iothub_queue_dropped_count",
                () => new Measurement<long>(_sinkBlockInputDroppedCount, this._metrics.TagList), "Messages",
                "Telemetry messages dropped due to overflow.");
            _meter.CreateObservableUpDownCounter("iiot_edge_publisher_iothub_queue_size",
                () => new Measurement<long>(_sinkBlock.InputCount, this._metrics.TagList), "Messages",
                "Telemetry messages queued for sending upstream.");
            _meter.CreateObservableUpDownCounter("iiot_edge_publisher_batch_input_queue_size",
                () => new Measurement<long>(_batchDataSetMessageBlock.OutputCount, _metrics.TagList), "Notifications",
                "Telemetry messages queued for sending upstream.");
            _meter.CreateObservableUpDownCounter("iiot_edge_publisher_encoding_input_queue_size",
                () => new Measurement<long>(_encodingBlock.InputCount, _metrics.TagList), "Notifications",
                "Telemetry messages queued for sending upstream.");
            _meter.CreateObservableUpDownCounter("iiot_edge_publisher_encoding_output_queue_size",
                () => new Measurement<long>(_encodingBlock.OutputCount, _metrics.TagList), "Messages",
                "Telemetry messages queued for sending upstream.");
            _meter.CreateObservableCounter("iiot_edge_publisher_sent_iot_messages",
                () => new Measurement<long>(_messagesSentCount, this._metrics.TagList), "Messages",
                "Number of IoT messages successfully sent to Sink (IoT Hub or Edge Hub).");
            _meter.CreateObservableGauge("iiot_edge_publisher_sent_iot_messages_per_second",
                () => new Measurement<double>(_messagesSentCount / UpTime, this._metrics.TagList), "Messages/second",
                "IoT messages/second sent to Sink (IoT Hub or Edge Hub).");
            _meter.CreateObservableGauge("iiot_edge_publisher_estimated_message_chunks_per_day",
                () => new Measurement<double>(_messagesSentCount, this._metrics.TagList), "Messages/day",
                "Estimated 4kb message chunks used from daily quota.");
        }

        static readonly Counter<long> kMessagesErrors = Diagnostics.Meter.CreateCounter<long>(
            "iiot_edge_publisher_failed_iot_messages", "messages", "Number of failures sending a network message.");
        static readonly Histogram<double> kSendingDuration = Diagnostics.Meter.CreateHistogram<double>(
            "iiot_edge_publisher_messages_duration", "milliseconds", "Histogram of message sending durations.");

        private const int kMaxQueueSize = 4096; // = 1 GB

        private double UpTime => (DateTime.UtcNow - _startTime).TotalSeconds;
        private long _messagesSentCount;
        private long _sinkBlockInputDroppedCount;
        private DateTime _dataFlowStartTime = DateTime.MinValue;
        private readonly int _maxNotificationsPerMessage;
        private readonly int _maxNetworkMessageSize;
        private readonly int _maxPublishQueueSize;
        private readonly Timer _batchTriggerIntervalTimer;
        private readonly TimeSpan _batchTriggerInterval;
        private readonly IOptions<PublisherOptions> _options;
        private readonly IMessageEncoder _messageEncoder;
        private readonly ILogger _logger;
        private readonly IWriterGroupDiagnostics? _diagnostics;
        private readonly bool _logNotifications;
        private readonly BatchBlock<IOpcUaSubscriptionNotification> _batchDataSetMessageBlock;
        private readonly TransformManyBlock<IOpcUaSubscriptionNotification[], (IEvent, Action)> _encodingBlock;
        private readonly ActionBlock<(IEvent, Action)> _sinkBlock;
        private readonly DateTime _startTime = DateTime.UtcNow;
        private readonly CancellationTokenSource _cts;
        private readonly IMetricsContext _metrics;
        private readonly IEventClient _eventClient;
        private readonly Meter _meter = Diagnostics.NewMeter();
    }
}
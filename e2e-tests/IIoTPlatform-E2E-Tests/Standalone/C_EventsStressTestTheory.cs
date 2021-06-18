// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace IIoTPlatform_E2E_Tests.Standalone {
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using FluentAssertions;
    using System.Diagnostics;
    using TestExtensions;
    using TestModels;
    using Xunit;
    using Xunit.Abstractions;
    using static System.TimeSpan;

    /// <summary>
    /// The test theory submitting a high load of event messages
    /// </summary>
    public class C_EventsStressTestTheory : DynamicAciTestBase {
        public C_EventsStressTestTheory(IIoTStandaloneTestContext context, ITestOutputHelper output)
            : base(context, output) {
        }

        [Fact, PriorityOrder(10)]
        public async void TestACI_VerifyEnd2EndThroughputAndLatency() {

            // Settings
            const int eventIntervalPerInstanceMs = 400;
            const int eventInstances = 40;
            const int instances = 10;
            const int nSeconds = 20;

            // Arrange
            await TestHelper.CreateSimulationContainerAsync(_context,
                new List<string> {"/bin/sh", "-c", $"./opcplc --autoaccept --ei={eventInstances} --er={eventIntervalPerInstanceMs} --pn=50000"},
                _timeoutToken,
                numInstances: instances);

            var messages = _consumer.ReadMessagesFromWriterIdAsync<SystemEventTypePayload>(_writerId, _timeoutToken);

            // Act
            var pnJson = _context.PublishedNodesJson(
                50000,
                _writerId,
                TestConstants.PublishedNodesConfigurations.SimpleEventFilter("i=2041")); // OPC-UA BaseEventType
            await TestHelper.SwitchToStandaloneModeAndPublishNodesAsync(_context, pnJson, _timeoutToken);

            // When the first message has been received for each simulator, the system is up and we
            // "let the flag fall" to start computing event rates.
            _output.WriteLine("Waiting for first message for each PLC");
            var seenPublishers = new HashSet<string>();
            await messages.TakeWhile(m => {
                        if (seenPublishers.Add(m.PublisherId)) {
                            _output.WriteLine($"Received first message for {m.PublisherId}");
                        }
                        return seenPublishers.Count < _context.PlcAciDynamicUrls.Length;
                    })
                .CountAsync(_timeoutToken);
                
            _output.WriteLine($"Consuming messages for {nSeconds} seconds");
            var sw = Stopwatch.StartNew();
            var eventData = await messages
                .TakeWhile(_ => sw.Elapsed < FromSeconds(nSeconds))

                // Get time of event attached Server node
                .Select(e => (e.EnqueuedTime, e.Messages["i=2253"].SourceTimestamp))
                .ToListAsync(_timeoutToken);
            
            // Assert throughput

            // Bin events by 1-second interval to compute event rate histogram
            var eventRatesBySecond = eventData
                .GroupBy(s => s.SourceTimestamp.Truncate(FromSeconds(1)))
                .Select(g => g.Count())
                .ToList();

            _output.WriteLine($"Event rates per second, by second: {string.Join(',', eventRatesBySecond)} e/s");

            eventRatesBySecond.Count.Should().BeGreaterThan(
                nSeconds * 70 / 100,
                "Publisher should produce data continuously");
            
            // Trim first few and last seconds of data, since Publisher polls PLCs
            // at different times
            eventRatesBySecond = eventRatesBySecond
                .Skip(6)
                .SkipLast(6)
                .ToList();
            
            var (average, stDev) = DescriptiveStats(eventRatesBySecond);

            const double expectedEventsPerSecond = instances * eventInstances * 1000d / eventIntervalPerInstanceMs;
            average.Should().BeApproximately(
                expectedEventsPerSecond,
                expectedEventsPerSecond / 10d,
                "Publisher should match PLC event rate");

            stDev.Should().BeLessThan(expectedEventsPerSecond / 3d, "Publisher should sustain PLC event rate");
            
            // Assert latency
            var end2EndLatency = eventData
                .Select(v => v.EnqueuedTime - v.SourceTimestamp)
                .ToList();
            end2EndLatency.Min().Should().BePositive();
            end2EndLatency.Average(v => v.TotalMilliseconds).Should().BeLessThan(8000);
        }

        private static (double average, double stDev) DescriptiveStats(IReadOnlyCollection<int> population) {
            var average = population.Average();
            var stDev = Math.Sqrt(population.Sum(v => (v - average) * (v - average)) / population.Count);
            return (average, stDev);
        }
    }
}
﻿// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace IIoTPlatformE2ETests.TestEventProcessor.Checkers
{
    using Microsoft.Extensions.Logging;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;

    /// <summary>
    /// Checker to validate that values of sequenceNumber are incrementally increasing.
    /// It will return number duplicates, dropped and reset counts in the recorded progression.
    /// </summary>
    sealed class SequenceNumberChecker : IDisposable
    {
        private readonly Dictionary<string, uint> _latestValue = new();
        private readonly Dictionary<string, uint> _duplicateValues = new();
        private readonly Dictionary<string, uint> _droppedValues = new();
        private readonly Dictionary<string, uint> _resetValues = new();
        private readonly SemaphoreSlim _lock;
        private readonly ILogger _logger;

        /// <summary>
        /// Constructor for SequenceNumberChecker.
        /// </summary>
        /// <param name="logger"></param>
        public SequenceNumberChecker(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _lock = new SemaphoreSlim(1, 1);
        }

        /// <summary>
        /// Processing the received event.
        /// </summary>
        /// <param name="dataSetWriterId"> DataSetWriterId associated with sequence number. </param>
        /// <param name="sequenceNumber"> Value of sequence number. </param>
        /// <exception cref="ArgumentNullException"></exception>
        public void ProcessEvent(string dataSetWriterId, uint? sequenceNumber)
        {
            var curValue = sequenceNumber ?? throw new ArgumentNullException(nameof(sequenceNumber));

            // Default value when dataSetWriterId is not detected.
            dataSetWriterId ??= "$null";

            _lock.Wait();
            try
            {
                if (!_latestValue.TryGetValue(dataSetWriterId, out var value))
                {
                    value = curValue;
                    // There is no previous value.
                    _latestValue.Add(dataSetWriterId, value);
                    _duplicateValues.Add(dataSetWriterId, 0);
                    _droppedValues.Add(dataSetWriterId, 0);
                    _resetValues.Add(dataSetWriterId, 0);
                    return;
                }

                if (curValue == value + 1)
                {
                    _latestValue[dataSetWriterId] = curValue;
                    return;
                }

                if (curValue == _latestValue[dataSetWriterId])
                {
                    _duplicateValues[dataSetWriterId]++;
                    _logger.LogWarning("Duplicate SequenceNumber for {DataSetWriterId} dataSetWriter detected: {Value}",
                        dataSetWriterId, curValue);
                    return;
                }

                if (curValue < _latestValue[dataSetWriterId])
                {
                    _resetValues[dataSetWriterId]++;
                    _logger.LogWarning("Reset SequenceNumber for {DataSetWriterId} dataSetWriter detected: previous {PrevValue} vs current {Value}",
                        dataSetWriterId, _latestValue[dataSetWriterId], curValue);
                    _latestValue[dataSetWriterId] = curValue;
                    return;
                }

                _droppedValues[dataSetWriterId] += curValue - _latestValue[dataSetWriterId];
                _logger.LogWarning("Dropped SequenceNumbers for {DataSetWriterId} dataSetWriter detected: {Count}: previous {PrevValue} vs current {CurValue}",
                    dataSetWriterId, curValue - _latestValue[dataSetWriterId], _latestValue[dataSetWriterId], curValue);
                _latestValue[dataSetWriterId] = curValue;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Stop processing and return the stored metrics.
        /// </summary>
        /// <returns></returns>
        public SequenceNumberCheckerResult Stop()
        {
            _lock.Wait();
            try
            {
                return new SequenceNumberCheckerResult()
                {
                    DroppedValueCount = (uint)_droppedValues.Sum(kvp => kvp.Value),
                    DuplicateValueCount = (uint)_duplicateValues.Sum(kvp => kvp.Value),
                    ResetsValueCount = (uint)_resetValues.Sum(kvp => kvp.Value)
                };
            }
            finally
            {
                _lock.Release();
            }
        }

        public void Dispose()
        {
            _lock.Dispose();
        }
    }
}

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;

// GPU utilization is a rate. Take one category snapshot per interval and
// compare each instance with its sample from the previous snapshot.
sealed class ClawGpuEngineSampler {
    readonly PerformanceCounterCategory category = new PerformanceCounterCategory("GPU Engine");
    Dictionary<string, CounterSample> previous = new Dictionary<string, CounterSample>(StringComparer.OrdinalIgnoreCase);

    public List<KeyValuePair<string, double>> Read() {
        InstanceDataCollection instances;
        try {
            instances = category.ReadCategory()["Utilization Percentage"];
        } catch (InvalidOperationException) {
            instances = null;
        } catch (Win32Exception) {
            instances = null;
        } catch (UnauthorizedAccessException) {
            instances = null;
        }

        var readings = new List<KeyValuePair<string, double>>(instances == null ? 0 : instances.Count);
        var current = new Dictionary<string, CounterSample>(readings.Capacity, StringComparer.OrdinalIgnoreCase);
        if (instances != null) {
            foreach (InstanceData instance in instances.Values) {
                string name = instance.InstanceName;
                var sample = instance.Sample;
                current[name] = sample;
                CounterSample old;
                if (!previous.TryGetValue(name, out old) || old.CounterType != sample.CounterType ||
                    sample.TimeStamp100nSec <= old.TimeStamp100nSec || sample.RawValue < old.RawValue) continue;
                double value = CounterSample.Calculate(old, sample);
                if (value >= 0 && !double.IsInfinity(value))
                    readings.Add(new KeyValuePair<string, double>(name, value));
            }
        }
        previous = current;
        return readings;
    }
}

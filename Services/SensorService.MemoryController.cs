using LibreHardwareMonitor.Hardware;

namespace XinSpect;

public sealed partial class SensorService
{
    private MemoryControllerSnapshot _controllerSnapshot = MemoryControllerSnapshot.Empty;
    public MemoryControllerSnapshot ControllerSnapshot => MemoryControllerService.WithFreshness(
        Volatile.Read(ref _controllerSnapshot), _timestamp(), MemoryControllerService.ApplicationCaptureMaxAge);

    // Root Poll owns _pollLock. This copies already-updated values and performs no further I/O.
    private void CaptureMemoryControllerPoll(DateTimeOffset at)
    {
        try
        {
            var observations = new List<MemoryControllerClockSample>();
            foreach (var cpu in _computer.Hardware.Where(h => h.HardwareType == HardwareType.Cpu
                && MemoryControllerService.ValidCpuId(h.Identifier.ToString())))
                foreach (var sensor in cpu.Sensors.Where(s => s.SensorType == SensorType.Clock
                    && s.Name is "Memory" or "Uncore" or "Fabric"))
                    observations.Add(new(cpu.Identifier.ToString(), cpu.Name, sensor.Identifier.ToString(),
                        sensor.Name, sensor.Value, at));
            Volatile.Write(ref _controllerSnapshot, MemoryControllerService.FromLhm(observations, _timestamp()));
        }
        catch (Exception ex)
        {
            // Metadata is optional. A failed projection must neither poison the main poll nor
            // look like a successful empty hardware inventory. Never retain the old numbers.
            Volatile.Write(ref _controllerSnapshot, MemoryControllerService.FailedCapture(at, ex.GetType().Name));
        }
    }

    private void InvalidateMemoryControllerPoll()
        => Volatile.Write(ref _controllerSnapshot, MemoryControllerService.Invalidated(Volatile.Read(ref _controllerSnapshot)));
}

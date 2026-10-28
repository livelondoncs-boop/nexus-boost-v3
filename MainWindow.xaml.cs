using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;
using System.Diagnostics;
using System.Management;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace NexusBoost;

public partial class MainWindow : Window
{
    private string? _previousPowerGuid;
    private readonly DispatcherTimer _monitorTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Computer _computer = new() { IsCpuEnabled = true, IsGpuEnabled = true };
    private readonly List<double> _cpuSamples = new();
    private readonly List<double> _ramSamples = new();
    private string _lastDetectedGame = "";
    private bool _hardwareMonitorOpen;

    public MainWindow()
    {
        InitializeComponent();
        try { _computer.Open(); _hardwareMonitorOpen = true; }
        catch (Exception ex) { Log($"Hardware sensors unavailable: {ex.Message}"); }
        Loaded += (_, _) =>
        {
            ScanSystem();
            SampleMonitor();
            _monitorTimer.Tick += (_, _) => SampleMonitor();
            _monitorTimer.Start();
        };
        Closed += (_, _) =>
        {
            _monitorTimer.Stop();
            try { if (_hardwareMonitorOpen) _computer.Close(); } catch { }
        };
    }

    private static string RunPowerCfg(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("powercfg.exe", args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p == null) return "Unable to start powercfg.";
            string output = p.StandardOutput.ReadToEnd();
            string error = p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            return p.ExitCode == 0 ? output : $"{output}\n{error}".Trim();
        }
        catch (Exception ex) { return $"Power configuration error: {ex.Message}"; }
    }

    private void ScanSystem()
    {
        try
        {
            CpuValue.Text = GetCpuName();
            GpuNameValue.Text = "GPU: " + GetGpuName();
            RamValue.Text = GetRam();
            WindowsValue.Text = GetWindowsName();
            OsBadge.Text = WindowsValue.Text;
            var active = RunPowerCfg("/getactivescheme");
            PowerValue.Text = ParsePlanName(active);
            ActivePlanText.Text = PowerValue.Text;
            PlanDetails.Text = ParseGuid(active) is string guid
                ? $"Active plan ID: {guid}\nRestore Previous can return to the saved plan during this session."
                : "Could not identify the active power plan. Check Windows power settings.";
            Log("System scan completed.");
        }
        catch (Exception ex) { Log($"Scan error: {ex.Message}"); }
    }

    private static string GetWindowsName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName")?.ToString() ?? "Windows";
            var display = key?.GetValue("DisplayVersion")?.ToString();
            return string.IsNullOrWhiteSpace(display) ? product : $"{product} {display}";
        }
        catch { return "Windows"; }
    }

    private static string GetCpuName()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (ManagementObject item in searcher.Get()) return item["Name"]?.ToString()?.Trim() ?? "Unknown CPU";
        }
        catch { }
        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "CPU information unavailable";
    }

    private static string GetGpuName()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            var names = new List<string>();
            foreach (ManagementObject item in searcher.Get())
            {
                var name = item["Name"]?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name)) names.Add(name);
            }
            return names.Count > 0 ? string.Join(", ", names.Take(2)) : "GPU unavailable";
        }
        catch { return "GPU info unavailable"; }
    }

    private static (double TotalGb, double FreeGb)? GetMemory()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
            foreach (ManagementObject item in searcher.Get())
            {
                double totalKb = Convert.ToDouble(item["TotalVisibleMemorySize"]);
                double freeKb = Convert.ToDouble(item["FreePhysicalMemory"]);
                return (totalKb / 1024 / 1024, freeKb / 1024 / 1024);
            }
        }
        catch { }
        return null;
    }

    private static string GetRam()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            foreach (ManagementObject item in searcher.Get())
                if (ulong.TryParse(item["TotalPhysicalMemory"]?.ToString(), out var bytes)) return $"{bytes / 1024d / 1024d / 1024d:0.0} GB";
        }
        catch { }
        return "Unavailable";
    }

    private void SampleMonitor()
    {
        try
        {
            double cpu = ReadCpuLoad();
            var memory = GetMemory();
            if (cpu >= 0)
            {
                CpuLoadValue.Text = $"CPU load: {cpu:0}%";
                CpuBar.Value = Math.Clamp(cpu, 0, 100);
                AddSample(_cpuSamples, cpu);
            }
            if (memory.HasValue && memory.Value.TotalGb > 0)
            {
                double used = memory.Value.TotalGb - memory.Value.FreeGb;
                double percent = Math.Clamp(used / memory.Value.TotalGb * 100, 0, 100);
                RamValue.Text = $"{percent:0}% used";
                RamSub.Text = $"{used:0.0} / {memory.Value.TotalGb:0.0} GB";
                RamBar.Value = percent;
                AddSample(_ramSamples, percent);
            }
            UpdateHardwareSensors();
            UpdateDetectedGames();
            RefreshSamples();
        }
        catch (Exception ex) { Log($"Monitor sample failed: {ex.Message}"); }
    }

    private static double ReadCpuLoad()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT LoadPercentage FROM Win32_Processor");
            double sum = 0; int count = 0;
            foreach (ManagementObject item in searcher.Get())
                if (item["LoadPercentage"] != null) { sum += Convert.ToDouble(item["LoadPercentage"]); count++; }
            return count > 0 ? sum / count : -1;
        }
        catch { return -1; }
    }

    private void UpdateHardwareSensors()
    {
        if (!_hardwareMonitorOpen) { GpuLoadValue.Text = "Sensor library unavailable"; GpuTempValue.Text = "GPU/CPU sensor readings unavailable"; return; }
        try
        {
            double? gpuLoad = null, gpuTemp = null, cpuTemp = null;
            foreach (var hardware in _computer.Hardware)
            {
                hardware.Update();
                foreach (var sub in hardware.SubHardware) sub.Update();
                var sensors = hardware.Sensors.Concat(hardware.SubHardware.SelectMany(s => s.Sensors));
                foreach (var sensor in sensors)
                {
                    if (sensor.Value is not float value) continue;
                    if ((hardware.HardwareType == HardwareType.GpuNvidia || hardware.HardwareType == HardwareType.GpuAmd || hardware.HardwareType == HardwareType.GpuIntel)
                        && sensor.SensorType == SensorType.Load && sensor.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase))
                        gpuLoad ??= value;
                    if ((hardware.HardwareType == HardwareType.GpuNvidia || hardware.HardwareType == HardwareType.GpuAmd || hardware.HardwareType == HardwareType.GpuIntel)
                        && sensor.SensorType == SensorType.Temperature) gpuTemp ??= value;
                    if (hardware.HardwareType == HardwareType.Cpu && sensor.SensorType == SensorType.Temperature) cpuTemp ??= value;
                }
            }
            GpuLoadValue.Text = gpuLoad.HasValue ? $"{gpuLoad.Value:0}% load" : "Load unavailable";
            GpuTempValue.Text = $"GPU: {(gpuTemp.HasValue ? $"{gpuTemp.Value:0} °C" : "temp unavailable")} · CPU: {(cpuTemp.HasValue ? $"{cpuTemp.Value:0} °C" : "temp unavailable")}";
        }
        catch { GpuLoadValue.Text = "Sensor read unavailable"; GpuTempValue.Text = "Driver / sensor support may be missing"; }
    }

    private static void AddSample(List<double> samples, double value)
    {
        samples.Add(value);
        if (samples.Count > 30) samples.RemoveAt(0);
    }

    private void RefreshSamples()
    {
        SamplesList.Items.Clear();
        int count = Math.Max(_cpuSamples.Count, _ramSamples.Count);
        for (int i = 0; i < count; i++)
        {
            int ci = _cpuSamples.Count - count + i, ri = _ramSamples.Count - count + i;
            string cpu = ci >= 0 ? $"{_cpuSamples[ci]:0}% CPU" : "CPU —";
            string ram = ri >= 0 ? $"{_ramSamples[ri]:0}% RAM" : "RAM —";
            SamplesList.Items.Add($"{i + 1:00}   {cpu}     {ram}");
        }
    }

    private void UpdateDetectedGames()
    {
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Counter-Strike 2"] = new[] { "cs2" },
            ["Fortnite"] = new[] { "FortniteClient-Win64-Shipping" },
            ["VALORANT"] = new[] { "VALORANT-Win64-Shipping", "VALORANT" },
            ["Apex Legends"] = new[] { "r5apex" },
            ["PUBG: Battlegrounds"] = new[] { "TslGame" },
            ["Minecraft Java"] = new[] { "javaw" }
        };
        var found = new List<string>();
        foreach (var game in map)
            foreach (var procName in game.Value)
                try { if (Process.GetProcessesByName(procName).Length > 0) { found.Add(game.Key); break; } } catch { }
        string result = found.Count == 0 ? "No supported game detected." : "Running: " + string.Join(", ", found);
        DetectedGamesText.Text = result;
        if (result != _lastDetectedGame && found.Count > 0) Log(result);
        _lastDetectedGame = result;
    }

    private static string? ParseGuid(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"(?i)\b[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}\b");
        return match.Success ? match.Value : null;
    }

    private static string ParsePlanName(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"\((.*?)\)");
        return match.Success ? match.Groups[1].Value : (text.Contains("error", StringComparison.OrdinalIgnoreCase) ? "Unavailable" : text.Trim());
    }

    private void ApplyPowerPlan(string targetGuid, string label)
    {
        if (string.IsNullOrWhiteSpace(targetGuid)) return;
        var activeText = RunPowerCfg("/getactivescheme");
        var activeGuid = ParseGuid(activeText);
        if (string.Equals(activeGuid, targetGuid, StringComparison.OrdinalIgnoreCase))
        {
            Log($"{label}: this power plan is already active.");
            MessageBox.Show("This power plan is already active.", "NEXUS BOOST", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(_previousPowerGuid) && !string.IsNullOrWhiteSpace(activeGuid)) _previousPowerGuid = activeGuid;
        var confirm = MessageBox.Show($"Apply the Windows power plan '{label}'?\n\nCurrent plan: {ParsePlanName(activeText)}\n\nThis changes the Windows power plan only. It does not guarantee higher FPS. You can restore the previous plan during this session.",
            "Confirm power plan change", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        var result = RunPowerCfg($"/setactive {targetGuid}");
        if (result.Contains("error", StringComparison.OrdinalIgnoreCase) || result.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            Log($"Could not apply {label}: {result}");
            MessageBox.Show($"Windows could not apply the plan.\n{result}", "NEXUS BOOST", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Log($"Applied Windows power plan: {label}.");
        ScanSystem();
    }

    private void MaxFps_Click(object sender, RoutedEventArgs e)
    {
        var plans = RunPowerCfg("/list");
        var high = FindPlanGuid(plans, "High performance");
        if (high == null) { MessageBox.Show("High performance plan not found. No changes made.\n\n" + plans, "NEXUS BOOST", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        ApplyPowerPlan(high, "High performance");
    }

    private void Balanced_Click(object sender, RoutedEventArgs e)
    {
        var balanced = FindPlanGuid(RunPowerCfg("/list"), "Balanced");
        if (balanced == null) { MessageBox.Show("Balanced plan not found. No changes made.", "NEXUS BOOST", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        ApplyPowerPlan(balanced, "Balanced");
    }

    private static string? FindPlanGuid(string output, string name)
    {
        foreach (var line in output.Split('\n'))
            if (line.Contains(name, StringComparison.OrdinalIgnoreCase) && ParseGuid(line) is string guid) return guid;
        return null;
    }

    private void ApplyGameProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var game = button.Tag?.ToString() ?? "Game";
        var combo = game switch { "CS2" => CsPreset, "Fortnite" => FortPreset, _ => ValPreset };
        var preset = (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "MAX FPS";
        ActiveProfileText.Text = $"{game} · {preset}";
        Log($"Selected {preset} profile for {game}.");
        if (preset == "BALANCED") Balanced_Click(sender, e);
        else
        {
            MessageBox.Show($"Profile selected: {game} — {preset}.\n\nThis build provides live CPU/RAM readings and attempts hardware sensor readings. It does not yet modify game graphics settings or GPU-driver options; FPS/frame-time telemetry is not integrated.",
                "Game profile", MessageBoxButton.OK, MessageBoxImage.Information);
            if (preset == "MAX FPS") MaxFps_Click(sender, e);
        }
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_previousPowerGuid))
        {
            MessageBox.Show("There is no previous power plan saved in this session.", "Restore Center", MessageBoxButton.OK, MessageBoxImage.Information);
            Log("Restore requested, but no previous plan is saved.");
            return;
        }
        string restoreGuid = _previousPowerGuid;
        ApplyPowerPlan(restoreGuid, "previously active plan");
        if (string.Equals(ParseGuid(RunPowerCfg("/getactivescheme")), restoreGuid, StringComparison.OrdinalIgnoreCase))
        {
            _previousPowerGuid = null;
            ActiveProfileText.Text = "Restored previous power plan";
        }
    }

    private void ShowPlans_Click(object sender, RoutedEventArgs e) => MessageBox.Show(RunPowerCfg("/list"), "Available Windows power plans", MessageBoxButton.OK, MessageBoxImage.Information);
    private void OpenPowerSettings_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:powersleep") { UseShellExecute = true }); }
        catch (Exception ex) { Log($"Could not open Windows settings: {ex.Message}"); }
    }
    private void Scan_Click(object sender, RoutedEventArgs e) => ScanSystem();
    private void Dashboard_Click(object sender, RoutedEventArgs e) => ScanSystem();
    private void Presets_Click(object sender, RoutedEventArgs e) => MaxFps_Click(sender, e);
    private void Games_Click(object sender, RoutedEventArgs e) => ActiveProfileText.Text = "Choose a game profile";
    private void Log(string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Log(message)); return; }
        LogList.Items.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
        while (LogList.Items.Count > 100) LogList.Items.RemoveAt(LogList.Items.Count - 1);
    }
}

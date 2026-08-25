using NAudio.CoreAudioApi;

namespace SalesSupport.Capture;

public sealed record AudioDeviceInfo(int Index, string Id, string Name, bool DefaultCommunications, bool DefaultMultimedia);

/// <summary>
/// WASAPI device enumeration. Defaults follow the Communications role — the device Teams
/// and softphones actually use — falling back to the Multimedia default (D1, D22).
/// </summary>
public static class AudioDevices
{
    public static IReadOnlyList<AudioDeviceInfo> ListMicrophones() => List(DataFlow.Capture);
    public static IReadOnlyList<AudioDeviceInfo> ListSpeakers() => List(DataFlow.Render);

    public static MMDevice GetMicrophone(string? selector) => Get(DataFlow.Capture, selector);
    public static MMDevice GetSpeaker(string? selector) => Get(DataFlow.Render, selector);

    private static IReadOnlyList<AudioDeviceInfo> List(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        var communicationsId = TryDefaultId(enumerator, flow, Role.Communications);
        var multimediaId = TryDefaultId(enumerator, flow, Role.Multimedia);

        return enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
            .Select((device, index) => new AudioDeviceInfo(
                index, device.ID, device.FriendlyName,
                device.ID == communicationsId, device.ID == multimediaId))
            .ToList();
    }

    private static MMDevice Get(DataFlow flow, string? selector)
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active).ToList();
        if (devices.Count == 0)
            throw new InvalidOperationException($"No active {(flow == DataFlow.Capture ? "microphone" : "speaker")} devices found.");

        if (selector is not null)
        {
            if (int.TryParse(selector, out var index) && index >= 0 && index < devices.Count)
                return devices[index];
            return devices.FirstOrDefault(d => d.FriendlyName.Contains(selector, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"No device matching '{selector}'. Use --list to see devices.");
        }

        var communicationsId = TryDefaultId(enumerator, flow, Role.Communications);
        if (communicationsId is not null && devices.FirstOrDefault(d => d.ID == communicationsId) is { } communications)
            return communications;
        var multimediaId = TryDefaultId(enumerator, flow, Role.Multimedia);
        return devices.FirstOrDefault(d => d.ID == multimediaId) ?? devices[0];
    }

    private static string? TryDefaultId(MMDeviceEnumerator enumerator, DataFlow flow, Role role)
    {
        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(flow, role);
            return device.ID;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}

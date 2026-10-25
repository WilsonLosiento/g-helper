using GHelper.Helpers;
using System.Runtime.InteropServices;

namespace GHelper.Gpu.NVidia;

// Takes the dGPU's PCI functions (graphics + HD audio) out of PnP before Eco cuts their power, and puts them back
// once the card is on the bus again. Cutting power under a loaded nvlddmkm leaves the next start failing with
// STATUS_INSUFFICIENT_RESOURCES (Code 43, "Standard half-state") until a reboot; a disabled device has no driver
// loaded when the power goes, and is only started after the link has settled.
public static class NvidiaPnp
{
    const string NvidiaPrefix = @"PCI\VEN_10DE&";
    const string ConfigIds = "gpu_pnp_disabled";

    const uint CR_SUCCESS = 0;
    const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x1;
    const uint CM_GETIDLIST_FILTER_PRESENT = 0x100;
    const uint CM_DISABLE_UI_NOT_OK = 0x4;
    const uint CM_DISABLE_PERSIST = 0x8;
    const uint CM_REENUMERATE_SYNCHRONOUS = 0x1;
    const uint DN_NEED_RESTART = 0x100;
    const uint CM_PROB_DISABLED = 22;
    const uint CM_DRP_CLASS = 0x8;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_Device_ID_List_SizeW(out uint pulLen, string pszFilter, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_Device_ID_ListW(string pszFilter, char[] Buffer, uint BufferLen, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string? pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    static extern uint CM_Disable_DevNode(uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    static extern uint CM_Enable_DevNode(uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    static extern uint CM_Reenumerate_DevNode(uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_DevNode_Registry_PropertyW(uint dnDevInst, uint ulProperty, out uint pulRegDataType, byte[] Buffer, ref uint pulLength, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    static extern uint CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);

    public static bool IsActive() => AppConfig.IsGracefulEco() && ProcessHelper.IsUserAdministrator();

    public static bool HasDisabled() => !string.IsNullOrEmpty(AppConfig.GetString(ConfigIds));

    static List<string> PresentNvidiaDevices()
    {
        var ids = new List<string>();
        uint flags = CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT;
        if (CM_Get_Device_ID_List_SizeW(out uint len, "PCI", flags) != CR_SUCCESS || len == 0) return ids;

        var buffer = new char[len];
        if (CM_Get_Device_ID_ListW("PCI", buffer, len, flags) != CR_SUCCESS) return ids;

        foreach (var id in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            if (id.StartsWith(NvidiaPrefix, StringComparison.OrdinalIgnoreCase)) ids.Add(id);

        // graphics first, so the audio function is the last to go and the first to come back
        return ids.OrderBy(id => IsDisplay(id) ? 0 : 1).ToList();
    }

    static bool IsDisplay(string id)
    {
        if (CM_Locate_DevNodeW(out uint devInst, id, 0) != CR_SUCCESS) return false;
        var buffer = new byte[256];
        uint len = (uint)buffer.Length;
        if (CM_Get_DevNode_Registry_PropertyW(devInst, CM_DRP_CLASS, out _, buffer, ref len, 0) != CR_SUCCESS) return false;
        return System.Text.Encoding.Unicode.GetString(buffer, 0, (int)len).TrimEnd('\0') == "Display";
    }

    static bool IsDisabled(uint devInst) =>
        CM_Get_DevNode_Status(out uint status, out uint problem, devInst, 0) == CR_SUCCESS &&
        problem == CM_PROB_DISABLED && (status & DN_NEED_RESTART) == 0;

    /// <summary>Disable the dGPU's functions. False (with everything re-enabled) when Windows refuses or wants a reboot.</summary>
    public static bool Disable()
    {
        var ids = PresentNvidiaDevices();
        if (ids.Count == 0)
        {
            Logger.WriteLine("dGPU PnP: no NVIDIA device present");
            return true;
        }

        var done = new List<string>();
        foreach (var id in ids)
        {
            uint cr = CM_Locate_DevNodeW(out uint devInst, id, 0);
            if (cr == CR_SUCCESS) cr = CM_Disable_DevNode(devInst, CM_DISABLE_UI_NOT_OK | CM_DISABLE_PERSIST);
            if (cr == CR_SUCCESS) done.Add(id);

            if (cr != CR_SUCCESS || !IsDisabled(devInst))
            {
                Logger.WriteLine($"dGPU PnP: disable {id} failed (CR {cr}), restoring");
                AppConfig.Set(ConfigIds, string.Join("|", done));
                EnableSaved();
                return false;
            }
            Logger.WriteLine($"dGPU PnP: disabled {id}");
        }

        AppConfig.Set(ConfigIds, string.Join("|", done));
        return true;
    }

    /// <summary>After power-on: wait for the functions to be back on the bus, then enable them, audio first.</summary>
    public static async Task Enable(int timeoutMs = 20000)
    {
        if (!HasDisabled()) return;

        var ids = AppConfig.GetString(ConfigIds).Split('|', StringSplitOptions.RemoveEmptyEntries);
        CM_Locate_DevNodeW(out uint root, null, 0);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            CM_Reenumerate_DevNode(root, CM_REENUMERATE_SYNCHRONOUS);
            if (ids.All(id => CM_Locate_DevNodeW(out _, id, 0) == CR_SUCCESS)) break;
            await Task.Delay(1000);
        }
        Logger.WriteLine($"dGPU PnP: on the bus after {watch.ElapsedMilliseconds} ms");

        // let the PCIe link settle before the driver starts
        await Task.Delay(AppConfig.Get("gpu_pnp_settle", 3000));
        EnableSaved();
    }

    static void EnableSaved()
    {
        var ids = (AppConfig.GetString(ConfigIds) ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries);
        var left = new List<string>();

        foreach (var id in ids.Reverse())
        {
            uint cr = CM_Locate_DevNodeW(out uint devInst, id, 0);
            if (cr == CR_SUCCESS) cr = CM_Enable_DevNode(devInst, 0);
            Logger.WriteLine($"dGPU PnP: enable {id} : CR {cr}");
            if (cr != CR_SUCCESS) left.Add(id);
            else Thread.Sleep(2000);
        }

        if (left.Count == 0) AppConfig.Remove(ConfigIds);
        else AppConfig.Set(ConfigIds, string.Join("|", left));
    }
}

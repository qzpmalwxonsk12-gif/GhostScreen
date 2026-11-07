using System.Reflection;

namespace GhostScreen;

internal sealed class BrightnessController
{
    private int? _savedBrightness;
    private bool _isDimmedByUs;

    public bool IsSupported { get; private set; }
    public int? SavedBrightness => _savedBrightness;

    public BrightnessController()
    {
        IsSupported = GetCurrentBrightness().HasValue;
    }

    public int? GetCurrentBrightness()
    {
        try
        {
            Type? locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (locatorType == null) return null;

            object? locator = Activator.CreateInstance(locatorType);
            if (locator == null) return null;

            object? service = locatorType.InvokeMember(
                "ConnectServer",
                BindingFlags.InvokeMethod,
                null,
                locator,
                new object[] { ".", @"root\WMI" });

            if (service == null) return null;

            object? items = service.GetType().InvokeMember(
                "ExecQuery",
                BindingFlags.InvokeMethod,
                null,
                service,
                new object[] { "SELECT CurrentBrightness FROM WmiMonitorBrightness" });

            if (items == null) return null;

            int count = Convert.ToInt32(items.GetType().InvokeMember(
                "Count",
                BindingFlags.GetProperty,
                null,
                items,
                null));

            if (count <= 0) return null;

            object? firstItem = items.GetType().InvokeMember(
                "ItemIndex",
                BindingFlags.InvokeMethod,
                null,
                items,
                new object[] { 0 });

            if (firstItem == null) return null;

            object? props = firstItem.GetType().InvokeMember(
                "Properties_",
                BindingFlags.GetProperty,
                null,
                firstItem,
                null);

            if (props == null) return null;

            object? propItem = props.GetType().InvokeMember(
                "Item",
                BindingFlags.InvokeMethod,
                null,
                props,
                new object[] { "CurrentBrightness" });

            if (propItem == null) return null;

            object? val = propItem.GetType().InvokeMember(
                "Value",
                BindingFlags.GetProperty,
                null,
                propItem,
                null);

            if (val == null) return null;
            return Convert.ToInt32(val);
        }
        catch
        {
            return null;
        }
    }

    public bool SetBrightness(int targetBrightness)
    {
        int clamped = Math.Clamp(targetBrightness, 0, 100);
        try
        {
            Type? locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (locatorType == null) return false;

            object? locator = Activator.CreateInstance(locatorType);
            if (locator == null) return false;

            object? service = locatorType.InvokeMember(
                "ConnectServer",
                BindingFlags.InvokeMethod,
                null,
                locator,
                new object[] { ".", @"root\WMI" });

            if (service == null) return false;

            object? methods = service.GetType().InvokeMember(
                "ExecQuery",
                BindingFlags.InvokeMethod,
                null,
                service,
                new object[] { "SELECT * FROM WmiMonitorBrightnessMethods" });

            if (methods == null) return false;

            int count = Convert.ToInt32(methods.GetType().InvokeMember(
                "Count",
                BindingFlags.GetProperty,
                null,
                methods,
                null));

            if (count <= 0) return false;

            for (int i = 0; i < count; i++)
            {
                object? methodObj = methods.GetType().InvokeMember(
                    "ItemIndex",
                    BindingFlags.InvokeMethod,
                    null,
                    methods,
                    new object[] { i });

                methodObj?.GetType().InvokeMember(
                    "WmiSetBrightness",
                    BindingFlags.InvokeMethod,
                    null,
                    methodObj,
                    new object[] { 1, clamped });
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public void DimToZero()
    {
        if (!IsSupported) return;

        int? current = GetCurrentBrightness();
        if (current.HasValue && (!_isDimmedByUs || current.Value > 0))
        {
            // Save original brightness (if it was 0 already, default restore to 80%)
            _savedBrightness = current.Value > 0 ? current.Value : 80;
        }

        if (SetBrightness(0))
        {
            _isDimmedByUs = true;
        }
    }

    public void RestoreBrightness()
    {
        if (!_isDimmedByUs) return;

        int restoreLevel = _savedBrightness ?? 80;
        SetBrightness(restoreLevel);
        _isDimmedByUs = false;
    }
}

// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/settings/PassthroughSetting.{h,cpp}: forwards reads/writes to another
// setting, gated so writes only reach the local storage while the gate is true.
using FlintLauncher.Core.Settings;

namespace FlintLauncher.Core.Settings;

public class PassthroughSetting : Setting
{
    private readonly Setting _other;
    private readonly Setting _gate;

    public PassthroughSetting(Setting original, Setting gate) : base(original.ConfigKeys, original.Default)
    {
        _other = original;
        _gate = gate;
    }

    private bool IsOverriding() => Variant.AsBool(_gate.Get());

    public override object? Default => IsOverriding() ? _other.Default : _other.Get();

    public override object? Get()
    {
        if (IsOverriding())
        {
            return base.Get();
        }
        return _other.Get();
    }

    public override void Set(object? value)
    {
        if (IsOverriding())
        {
            base.Set(value);
        }
        _other.Set(value);
    }

    public override void Reset()
    {
        if (IsOverriding())
        {
            base.Reset();
        }
        _other.Reset();
    }
}

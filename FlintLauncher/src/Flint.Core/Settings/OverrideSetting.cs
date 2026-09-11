// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/settings/OverrideSetting.{h,cpp}: value comes from this setting when the
// gate is true, otherwise from the overridden (usually global) setting.
using Flint.Core.Settings;

namespace Flint.Core.Settings;

public class OverrideSetting : Setting
{
    private readonly Setting _other;
    private readonly Setting _gate;

    public OverrideSetting(Setting overridden, Setting gate) : base(overridden.ConfigKeys, overridden.Default)
    {
        _other = overridden;
        _gate = gate;
    }

    private bool IsOverriding() => Variant.AsBool(_gate.Get());

    public override object? Default => _other.Get();

    public override object? Get()
    {
        if (IsOverriding())
        {
            return base.Get();
        }
        return _other.Get();
    }

    public override void Set(object? value) => base.Set(value);

    public override void Reset() => base.Reset();
}

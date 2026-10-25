// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FluentFlyoutWPF.Models;

public class AudioDeviceModel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }

    public override string ToString() => Name;

    public override bool Equals(object? obj) => obj is AudioDeviceModel other && Id == other.Id;
    public override int GetHashCode() => Id.GetHashCode();
}

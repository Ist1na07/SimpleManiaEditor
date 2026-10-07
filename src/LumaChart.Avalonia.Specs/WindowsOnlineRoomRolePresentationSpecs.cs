using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia.Specs;

internal static class WindowsOnlineRoomRolePresentationSpecs
{
    public static WindowsOnlineRoomRolePresentationSpecResult Run()
    {
        var options =
            WindowsOnlineRoomRolePresentationPolicy.EditableRoleOptions;
        var passed =
            WindowsOnlineRoomRolePresentationPolicy.Contract ==
            "wpf-1.1.0-online-room-role-presentation-r1" &&
            WindowsOnlineRoomRolePresentationPolicy.RoleLabel("OWNER") == "房主" &&
            WindowsOnlineRoomRolePresentationPolicy.RoleLabel("editor") == "编辑者" &&
            WindowsOnlineRoomRolePresentationPolicy.RoleLabel(" viewer ") == "观察者" &&
            WindowsOnlineRoomRolePresentationPolicy.RoleLabel("unknown") == "编辑者" &&
            WindowsOnlineRoomRolePresentationPolicy.MemberStateLabel(
                "editor",
                isOnline: true) == "编辑者" &&
            WindowsOnlineRoomRolePresentationPolicy.MemberStateLabel(
                "viewer",
                isOnline: false) == "观察者 · 离线" &&
            options.Count == 2 &&
            options[0].Value == "editor" &&
            options[0].Label == "编辑者" &&
            options[0].ToString() == "编辑者" &&
            options[1].Value == "viewer" &&
            options[1].Label == "观察者" &&
            options[1].ToString() == "观察者" &&
            WindowsOnlineRoomRolePresentationPolicy.EditableOption("EDITOR") ==
            options[0] &&
            WindowsOnlineRoomRolePresentationPolicy.EditableOption("viewer") ==
            options[1] &&
            WindowsOnlineRoomRolePresentationPolicy.EditableRoleValue("编辑者") ==
            "editor" &&
            WindowsOnlineRoomRolePresentationPolicy.EditableRoleValue("观察者") ==
            "viewer" &&
            WindowsOnlineRoomRolePresentationPolicy.ChangeActionLabel("editor") ==
            "设为观察者" &&
            WindowsOnlineRoomRolePresentationPolicy.ChangeActionLabel("viewer") ==
            "设为编辑者" &&
            WindowsOnlineRoomRolePresentationPolicy.ToggleTargetRole("editor") ==
            "viewer" &&
            WindowsOnlineRoomRolePresentationPolicy.ToggleTargetRole("viewer") ==
            "editor" &&
            WindowsOnlineRoomRolePresentationPolicy.PermissionHeading ==
            "房间编辑权限 · 由房主持久分配" &&
            WindowsOnlineRoomRolePresentationPolicy.MetadataPermissionLabel ==
            "允许编辑谱面信息" &&
            WindowsOnlineRoomRolePresentationPolicy.TimingPermissionLabel ==
            "允许编辑 Effect（Timing Edit 全部功能）" &&
            WindowsOnlineRoomRolePresentationPolicy.NotesPermissionLabel ==
            "允许编辑 Note" &&
            WindowsOnlineRoomRolePresentationPolicy.SavePermissionsLabel ==
            "保存房间权限" &&
            WindowsOnlineRoomRolePresentationPolicy.KickLabel == "踢出" &&
            WindowsOnlineRoomRolePresentationPolicy.TemporaryBanLabel ==
            "暂时封禁 30 分钟" &&
            WindowsOnlineRoomRolePresentationPolicy.TemporaryBanMinutes == 30;
        return new WindowsOnlineRoomRolePresentationSpecResult(
            "online-room-role-visible-copy-matches-wpf-110",
            passed,
            $"roles={string.Join('/', options.Select(option => option.Label))}; " +
            $"state={WindowsOnlineRoomRolePresentationPolicy.MemberStateLabel("viewer", false)}; " +
            $"actions={WindowsOnlineRoomRolePresentationPolicy.ChangeActionLabel("editor")}/" +
            $"{WindowsOnlineRoomRolePresentationPolicy.ChangeActionLabel("viewer")}; " +
            $"ban={WindowsOnlineRoomRolePresentationPolicy.TemporaryBanLabel}");
    }
}

internal sealed record WindowsOnlineRoomRolePresentationSpecResult(
    string Name,
    bool Passed,
    string Detail);

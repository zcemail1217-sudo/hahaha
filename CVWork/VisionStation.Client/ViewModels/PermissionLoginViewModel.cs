using System.Collections.ObjectModel;
using Prism.Commands;
using Prism.Mvvm;
using VisionStation.Domain;

namespace VisionStation.Client.ViewModels;

public sealed class PermissionLoginViewModel : BindableBase
{
    private string _userName = "operator";
    private string _accessCode = string.Empty;
    private AccessRoleItem? _selectedRole;
    private string _currentUser = "未登录";
    private string _currentRole = "-";
    private string _loginStatusText = "等待登录";

    public PermissionLoginViewModel(DeviceConfiguration configuration)
    {
        var accessControl = configuration.SystemSettings.AccessControl;
        LoginRequiredText = accessControl.LoginRequired ? "启用" : "关闭";
        SessionTimeoutText = $"{accessControl.SessionTimeoutMinutes} 分钟";

        foreach (var role in accessControl.Roles)
        {
            Roles.Add(new AccessRoleItem(role.Key, role.Name, role.Description));
        }

        if (Roles.Count == 0)
        {
            Roles.Add(new AccessRoleItem("Operator", "操作员", "生产运行和查看权限"));
            Roles.Add(new AccessRoleItem("Engineer", "工程师", "配方、流程和参数维护权限"));
            Roles.Add(new AccessRoleItem("Administrator", "管理员", "系统设置和权限管理权限"));
        }

        SelectedRole = Roles.FirstOrDefault(role => string.Equals(role.Key, accessControl.DefaultRole, StringComparison.OrdinalIgnoreCase))
            ?? Roles.FirstOrDefault();

        LoginCommand = new DelegateCommand(Login);
        LogoutCommand = new DelegateCommand(Logout);
    }

    public ObservableCollection<AccessRoleItem> Roles { get; } = new();

    public DelegateCommand LoginCommand { get; }

    public DelegateCommand LogoutCommand { get; }

    public string LoginRequiredText { get; }

    public string SessionTimeoutText { get; }

    public string UserName
    {
        get => _userName;
        set => SetProperty(ref _userName, value);
    }

    public string AccessCode
    {
        get => _accessCode;
        set => SetProperty(ref _accessCode, value);
    }

    public AccessRoleItem? SelectedRole
    {
        get => _selectedRole;
        set => SetProperty(ref _selectedRole, value);
    }

    public string CurrentUser
    {
        get => _currentUser;
        private set => SetProperty(ref _currentUser, value);
    }

    public string CurrentRole
    {
        get => _currentRole;
        private set => SetProperty(ref _currentRole, value);
    }

    public string LoginStatusText
    {
        get => _loginStatusText;
        private set => SetProperty(ref _loginStatusText, value);
    }

    private void Login()
    {
        if (string.IsNullOrWhiteSpace(UserName))
        {
            LoginStatusText = "请输入账号。";
            return;
        }

        if (SelectedRole is null)
        {
            LoginStatusText = "请选择权限角色。";
            return;
        }

        CurrentUser = UserName.Trim();
        CurrentRole = SelectedRole.Name;
        LoginStatusText = string.IsNullOrWhiteSpace(AccessCode)
            ? "已按预留入口登录，后续可接入密码校验。"
            : "登录完成。";
    }

    private void Logout()
    {
        CurrentUser = "未登录";
        CurrentRole = "-";
        AccessCode = string.Empty;
        LoginStatusText = "已退出登录。";
    }
}

public sealed record AccessRoleItem(string Key, string Name, string Description)
{
    public override string ToString()
    {
        return Name;
    }
}

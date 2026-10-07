using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System.Text.Json;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Online;

namespace LumaChart.Avalonia.Dialogs;

/// <summary>
/// Windows account surface matching the WPF login, registration and password
/// recovery workflows. Text fields intentionally have no Enter/Escape global
/// handlers so active Chinese IME composition is committed by explicit focus
/// change before an action button is invoked.
/// </summary>
public sealed class OnlineAccountDialog : PlatformWindow
{
    internal const string HostedDialogAutomationId = "OnlineAccount.Dialog";

    private static readonly IBrush PanelBrush = Brush("#181C1F");
    private static readonly IBrush RaisedBrush = Brush("#20282E");
    private static readonly IBrush StrokeBrush = Brush("#39464D");
    private static readonly IBrush AccentBrush = Brush("#79B8BD");
    private static readonly IBrush MutedBrush = Brush("#89989E");
    private static readonly IBrush ErrorBrush = Brush("#D39A88");

    private readonly OnlineAccountSession session;
    private readonly Action<bool>? hostedCompletion;
    private readonly Func<OnlineApiException, Task>? clientUpdateRequired;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Grid root = new();
    private readonly TextBox loginUsername = TextField(24, "OnlineAccount.Login.Username");
    private readonly TextBox loginPassword = PasswordField("OnlineAccount.Login.Password");
    private readonly TextBox loginCaptchaAnswer = TextField(8, "OnlineAccount.Login.CaptchaAnswer");
    private readonly Image loginCaptchaImage = CaptchaImage("OnlineAccount.Login.CaptchaImage");
    private readonly TextBlock loginStatus = StatusText("OnlineAccount.Login.Status");
    private readonly Button loginRefreshButton = ActionButton("刷新", "OnlineAccount.Login.CaptchaRefresh");
    private readonly Button loginSubmitButton = PrimaryButton("登录", "OnlineAccount.Login.Submit");
    private readonly Button osuLoginButton = OsuButton(
        "使用 osu! 注册或登录",
        "OnlineAccount.Login.Osu");
    private readonly Button registerOpenButton = ActionButton("注册账号", "OnlineAccount.Login.Register");
    private readonly Button resetOpenButton = LinkButton("忘记密码？", "OnlineAccount.Login.ResetPassword");

    private readonly StackPanel registrationFields = new() { Spacing = 5 };
    private readonly TextBox workflowUsername = TextField(24, "OnlineAccount.Workflow.Username");
    private readonly TextBox workflowDisplayName = TextField(32, "OnlineAccount.Workflow.DisplayName");
    private readonly TextBox workflowEmail = TextField(254, "OnlineAccount.Workflow.Email");
    private readonly TextBox workflowCaptchaAnswer = TextField(8, "OnlineAccount.Workflow.CaptchaAnswer");
    private readonly Image workflowCaptchaImage = CaptchaImage("OnlineAccount.Workflow.CaptchaImage");
    private readonly TextBox workflowEmailCode = TextField(6, "OnlineAccount.Workflow.EmailCode");
    private readonly TextBox workflowPassword = PasswordField("OnlineAccount.Workflow.Password");
    private readonly TextBox workflowPasswordConfirm = PasswordField("OnlineAccount.Workflow.PasswordConfirm");
    private readonly TextBlock workflowTitle = new();
    private readonly TextBlock workflowDescription = new();
    private readonly TextBlock workflowStatus = StatusText("OnlineAccount.Workflow.Status");
    private readonly TextBlock identityStatus = StatusText("OnlineAccount.Identity.Status");
    private readonly Button workflowRefreshButton = ActionButton("刷新", "OnlineAccount.Workflow.CaptchaRefresh");
    private readonly Button workflowSendCodeButton = ActionButton("发送验证码", "OnlineAccount.Workflow.SendEmailCode");
    private readonly Button workflowSubmitButton = PrimaryButton("注册并登录", "OnlineAccount.Workflow.Submit");

    private Guid loginCaptchaId;
    private Guid workflowCaptchaId;
    private string workflowMode = "register";
    private bool actionInProgress;
    private bool loginCaptchaLoadInProgress;
    private bool workflowCaptchaLoadInProgress;
    private Bitmap? loginCaptchaBitmap;
    private Bitmap? workflowCaptchaBitmap;
    private Control? loginSurface;
    private Control? workflowSurface;
    private int loginRefreshGeneration;
    private int workflowRefreshGeneration;
    private int emailCooldownGeneration;
    private bool activated;
    private bool disposed;
    private bool completed;

    public OnlineAccountDialog(
        OnlineAccountSession session,
        Action<bool>? hostedCompletion = null)
        : this(
            session,
            hostedCompletion,
            clientUpdateRequired: null,
            extendedHostContract: true)
    {
    }

    internal OnlineAccountDialog(
        OnlineAccountSession session,
        Action<bool>? hostedCompletion,
        Func<OnlineApiException, Task>? clientUpdateRequired)
        : this(
            session,
            hostedCompletion,
            clientUpdateRequired,
            extendedHostContract: true)
    {
    }

    private OnlineAccountDialog(
        OnlineAccountSession session,
        Action<bool>? hostedCompletion,
        Func<OnlineApiException, Task>? clientUpdateRequired,
        bool extendedHostContract)
    {
        _ = extendedHostContract;
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.hostedCompletion = hostedCompletion;
        this.clientUpdateRequired = clientUpdateRequired;
        Title = "LumaChart Online";
        Width = 830;
        Height = 830;
        MinWidth = 700;
        MinHeight = 650;
        Background = Brush("#111315");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = true;
        AutomationProperties.SetAutomationId(this, HostedDialogAutomationId);
        AutomationProperties.SetAutomationId(workflowTitle, "OnlineAccount.Workflow.Title");
        AutomationProperties.SetAutomationId(
            registrationFields,
            "OnlineAccount.Workflow.RegistrationFields");

        loginRefreshButton.Click += async (_, _) => await LoadLoginCaptchaAsync();
        loginSubmitButton.Click += async (_, _) => await LoginAsync();
        osuLoginButton.Click += async (_, _) => await AuthorizeWithOsuAsync();
        registerOpenButton.Click += async (_, _) => await ShowWorkflowAsync("register");
        resetOpenButton.Click += async (_, _) => await ShowWorkflowAsync("reset");
        workflowRefreshButton.Click += async (_, _) => await LoadWorkflowCaptchaAsync();
        workflowSendCodeButton.Click += async (_, _) => await SendEmailCodeAsync();
        workflowSubmitButton.Click += async (_, _) => await SubmitWorkflowAsync();

        Content = root;
        Opened += async (_, _) => await ActivateAsync();
        Closed += (_, _) => DisposeSurface();
    }

    internal Control DetachHostedContent()
    {
        if (hostedCompletion == null)
            throw new InvalidOperationException(
                "Hosted content requires a hosted completion callback.");
        if (Content is not Control content)
            throw new InvalidOperationException("The account surface has no content.");
        Content = null;
        var hosted = new AutomationBorder { Child = content };
        AutomationProperties.SetAutomationId(hosted, HostedDialogAutomationId);
        AutomationProperties.SetName(hosted, "LumaChart Online 登录");
        return hosted;
    }

    internal Task ActivateHostedAsync() => ActivateAsync();

    internal void DisposeHostedSurface() => DisposeSurface();

    private async Task ActivateAsync()
    {
        if (activated || disposed) return;
        activated = true;
        if (session.IsAuthenticated)
        {
            ShowIdentity();
            return;
        }

        ShowLogin();
        await LoadLoginCaptchaAsync();
    }

    private void Complete(bool result)
    {
        if (completed) return;
        completed = true;
        if (hostedCompletion != null)
        {
            hostedCompletion(result);
            return;
        }

        Close(result);
    }

    private void DisposeSurface()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        loginCaptchaBitmap?.Dispose();
        workflowCaptchaBitmap?.Dispose();
        // lifetime is cancelled but deliberately not disposed: requests that
        // were in flight resume after this point, and their catch/finally paths
        // (captcha reload, cooldowns) still read lifetime.Token. On a disposed
        // source that throws ObjectDisposedException out of an async Click
        // handler, which closes the application.
    }

    private void ShowLogin()
    {
        root.Children.Clear();
        loginSurface ??= BuildLoginSurface();
        root.Children.Add(loginSurface);
#if !LUMACHART_IOS
        // A ready caret is right on desktop. On a tablet this surface appears by
        // itself at start-up, and focusing a text box raises the soft keyboard
        // over the lower half of the card — which is where the captcha image,
        // its input and the login button are. The user cannot read the digits
        // they are being asked to type. They get the keyboard by tapping the
        // field, at which point the card scrolls into view on its own.
        loginUsername.Focus();
#endif
    }

    private Control BuildLoginSurface()
    {
        var layout = new Grid
        {
            Width = WindowsOnlineSurfaceParityPolicy.LoginCardWidth,
            MaxWidth = WindowsOnlineSurfaceParityPolicy.LoginCardWidth,
            Height = WindowsOnlineSurfaceParityPolicy.LoginCardHeight,
            MaxHeight = WindowsOnlineSurfaceParityPolicy.LoginCardHeight,
            ColumnDefinitions = new ColumnDefinitions(
                $"{WindowsOnlineSurfaceParityPolicy.LoginIdentityColumnWidth},*"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        layout.Children.Add(new Border
        {
            Background = Brush("#15191B"),
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(1, 1, 0, 1),
            CornerRadius = new CornerRadius(7, 0, 0, 7),
            Padding = new Thickness(
                WindowsOnlineSurfaceParityPolicy.LoginIdentityPadding),
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Spacing = 0,
                Children =
                {
                    new WindowsDefaultUserAvatar(
                        WindowsOnlineSurfaceParityPolicy.LoginAvatarSize,
                        Brush(WindowsOnlineSurfaceParityPolicy.LoginAvatarBorderColor),
                        WindowsOnlineSurfaceParityPolicy.LoginAvatarBorderThickness,
                        "OnlineAccount.Login.DefaultAvatar")
                    {
                        HorizontalAlignment = HorizontalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = "LumaChart Online",
                        FontSize = 23,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        TextAlignment = TextAlignment.Center,
                        Margin = new Thickness(
                            0,
                            WindowsOnlineSurfaceParityPolicy.LoginTitleTopMargin,
                            0,
                            0)
                    },
                    new TextBlock
                    {
                        Text = "账号、个人制谱时长与联机协作。你的本地谱面、音频和背景不会上传为云备份。",
                        Foreground = MutedBrush,
                        TextWrapping = TextWrapping.Wrap,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        TextAlignment = TextAlignment.Center,
                        LineHeight = 20,
                        Margin = new Thickness(
                            0,
                            WindowsOnlineSurfaceParityPolicy.LoginDescriptionTopMargin,
                            0,
                            0)
                    }
                }
            }
        });

        var form = new StackPanel
        {
            Margin = new Thickness(
                WindowsOnlineSurfaceParityPolicy.LoginFormLeftRightMargin,
                WindowsOnlineSurfaceParityPolicy.LoginFormTopMargin,
                WindowsOnlineSurfaceParityPolicy.LoginFormLeftRightMargin,
                WindowsOnlineSurfaceParityPolicy.LoginFormBottomMargin),
            Spacing = 7
        };
        form.Children.Add(Heading("登录"));
        form.Children.Add(Label("用户名"));
        form.Children.Add(loginUsername);
        form.Children.Add(Label("密码"));
        form.Children.Add(loginPassword);
        form.Children.Add(Label("人机验证"));
        form.Children.Add(BuildCaptchaRow(loginCaptchaImage, loginCaptchaAnswer, loginRefreshButton));
        resetOpenButton.HorizontalAlignment = HorizontalAlignment.Right;
        form.Children.Add(resetOpenButton);
        form.Children.Add(loginStatus);
        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Margin = new Thickness(0, 5, 0, 0) };
        loginSubmitButton.Margin = new Thickness(0, 0, 5, 0);
        loginSubmitButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        registerOpenButton.Margin = new Thickness(5, 0, 0, 0);
        registerOpenButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(registerOpenButton, 1);
        actions.Children.Add(loginSubmitButton);
        actions.Children.Add(registerOpenButton);
        form.Children.Add(actions);
        form.Children.Add(new TextBlock
        {
            Text = "或",
            Foreground = MutedBrush,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 1)
        });
        osuLoginButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        form.Children.Add(osuLoginButton);
        var right = Card(form, new CornerRadius(0, 7, 7, 0));
        Grid.SetColumn(right, 1);
        layout.Children.Add(right);
        return layout;
    }

    private async Task ShowWorkflowAsync(string mode)
    {
        if (actionInProgress) return;
        workflowMode = mode;
        workflowCaptchaId = Guid.Empty;
        workflowCaptchaAnswer.Text = string.Empty;
        workflowEmail.Text = string.Empty;
        workflowEmailCode.Text = string.Empty;
        workflowStatus.Text = string.Empty;
        workflowPassword.Text = loginPassword.Text;
        workflowPasswordConfirm.Text = loginPassword.Text;
        workflowUsername.Text = loginUsername.Text?.Trim();
        workflowDisplayName.Text = workflowUsername.Text;
        registrationFields.IsVisible = mode == "register";
        workflowTitle.Text = mode == "register" ? "注册账号" : "找回密码";
        workflowDescription.Text = mode == "register"
            ? "验证邮箱后创建账号；邮箱只用于账号安全与找回密码。"
            : "向账号已验证的邮箱发送验证码，重置后旧登录会话全部失效。";
        workflowSubmitButton.Content = mode == "register" ? "注册并登录" : "重置密码";
        root.Children.Clear();
        workflowSurface ??= BuildWorkflowSurface();
        root.Children.Add(workflowSurface);
        if (mode == "register") workflowUsername.Focus();
        else workflowEmail.Focus();
        await LoadWorkflowCaptchaAsync();
    }

    private Control BuildWorkflowSurface()
    {
        registrationFields.Children.Clear();
        registrationFields.Children.Add(Label("用户名（3–24 位英文、数字或下划线）"));
        registrationFields.Children.Add(workflowUsername);
        registrationFields.Children.Add(Label("显示名称"));
        registrationFields.Children.Add(workflowDisplayName);

        workflowTitle.FontSize = 23;
        workflowTitle.FontWeight = FontWeight.SemiBold;
        workflowTitle.Foreground = Brushes.White;
        workflowDescription.Foreground = MutedBrush;
        workflowDescription.TextWrapping = TextWrapping.Wrap;
        var form = new StackPanel { Spacing = 7 };
        form.Children.Add(workflowTitle);
        form.Children.Add(workflowDescription);
        form.Children.Add(registrationFields);
        form.Children.Add(Label("邮箱"));
        form.Children.Add(workflowEmail);
        form.Children.Add(Label("人机验证"));
        form.Children.Add(BuildCaptchaRow(workflowCaptchaImage, workflowCaptchaAnswer, workflowRefreshButton));
        var emailCode = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var emailCodeField = new StackPanel { Spacing = 4 };
        emailCodeField.Children.Add(Label("邮箱验证码"));
        emailCodeField.Children.Add(workflowEmailCode);
        workflowSendCodeButton.Margin = new Thickness(9, 20, 0, 0);
        Grid.SetColumn(workflowSendCodeButton, 1);
        emailCode.Children.Add(emailCodeField);
        emailCode.Children.Add(workflowSendCodeButton);
        form.Children.Add(emailCode);
        form.Children.Add(Label("新密码（10–128 个字符）"));
        form.Children.Add(workflowPassword);
        form.Children.Add(Label("确认密码"));
        form.Children.Add(workflowPasswordConfirm);
        form.Children.Add(workflowStatus);

        var cancel = ActionButton("取消", "OnlineAccount.Workflow.Cancel");
        cancel.Click += (_, _) =>
        {
            emailCooldownGeneration++;
            workflowRefreshGeneration++;
            ShowLogin();
        };
        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Margin = new Thickness(0, 7, 0, 0) };
        cancel.Margin = new Thickness(0, 0, 6, 0);
        cancel.Height = 40;
        cancel.HorizontalAlignment = HorizontalAlignment.Stretch;
        workflowSubmitButton.Margin = new Thickness(6, 0, 0, 0);
        workflowSubmitButton.Height = 40;
        workflowSubmitButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(workflowSubmitButton, 1);
        actions.Children.Add(cancel);
        actions.Children.Add(workflowSubmitButton);
        form.Children.Add(actions);

        return new Border
        {
            Width = WindowsOnlineSurfaceParityPolicy.WorkflowCardWidth,
            MaxHeight = WindowsOnlineSurfaceParityPolicy.WorkflowCardMaxHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = PanelBrush,
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(28),
            Child = new ScrollViewer
            {
                Content = form,
                VerticalScrollBarVisibility =
                    global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            }
        };
    }

    private void ShowIdentity()
    {
        var user = session.CurrentUser;
        if (user == null)
        {
            ShowLogin();
            return;
        }

        var logout = ActionButton("退出登录", "OnlineAccount.Identity.Logout");
        logout.Click += async (_, _) => await LogoutAsync();
        var close = PrimaryButton("完成", "OnlineAccount.Identity.Close");
        close.Click += (_, _) => Complete(true);
        var form = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Heading("账号已登录"),
                new TextBlock
                {
                    Text = user.DisplayName,
                    FontSize = 28,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Brushes.White
                },
                IdentityLine("用户名", user.Username),
                IdentityLine("账号 ID", user.Id.ToString()),
                IdentityLine("角色", user.Role),
                IdentityLine("邮箱", string.IsNullOrWhiteSpace(user.Email) ? "未绑定（可选）" : user.Email),
                IdentityLine(
                    "osu!",
                    user.OsuAccount == null
                        ? "未绑定 osu! 账户"
                        : $"{user.OsuAccount.Username} · {user.OsuAccount.ProfileUrl}"),
                IdentityLine("制谱时长", TimeSpan.FromSeconds(Math.Max(0, user.ChartingSeconds)).ToString(@"d\天\ hh\:mm\:ss")),
                new TextBlock
                {
                    Text = "登录会话已使用系统安全存储绑定到当前设备；不会保存明文密码。",
                    Foreground = MutedBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 4)
                },
                identityStatus
            }
        };
        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Margin = new Thickness(0, 10, 0, 0) };
        logout.Margin = new Thickness(0, 0, 6, 0);
        close.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(close, 1);
        actions.Children.Add(logout);
        actions.Children.Add(close);
        form.Children.Add(actions);
        root.Children.Clear();
        root.Children.Add(new Border
        {
            Width = 500,
            Padding = new Thickness(34),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = PanelBrush,
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Child = form
        });
    }

    private async Task LoadLoginCaptchaAsync()
    {
        // Several flows reload the captcha after an await (failed login,
        // logout, reset); the surface may have closed in between.
        if (disposed || loginCaptchaLoadInProgress || !loginRefreshButton.IsEnabled) return;
        loginCaptchaLoadInProgress = true;
        loginCaptchaId = Guid.Empty;
        loginCaptchaAnswer.Text = string.Empty;
        loginRefreshButton.IsEnabled = false;
        loginRefreshButton.Content = "载入中";
        loginStatus.Text = "正在载入登录验证码…";
        try
        {
            using var timeout = CreateTimeout(TimeSpan.FromSeconds(8));
            var challenge = await session.GetCaptchaAsync(timeout.Token);
            // DisposeSurface already released the captcha bitmaps.
            if (disposed) return;
            loginCaptchaId = challenge.Id;
            ReplaceBitmap(loginCaptchaImage, ref loginCaptchaBitmap, challenge.ImageBase64);
            loginStatus.Text = "请输入用户名、密码和图片中的 5 位数字。";
            loginStatus.Foreground = MutedBrush;
        }
        catch (OnlineApiException exception) when (
            exception.Code == "client_update_required")
        {
            loginStatus.Text = exception.Message;
            loginStatus.Foreground = ErrorBrush;
            loginCaptchaId = Guid.Empty;
            loginCaptchaAnswer.Text = string.Empty;
            if (clientUpdateRequired != null)
                await clientUpdateRequired(exception);
        }
        catch (Exception exception) when (IsExpectedOnlineFailure(exception))
        {
            loginStatus.Text = AvaloniaLocalizationCatalog.Format("登录验证码载入失败：{0}", FormatFailure(exception));
            loginStatus.Foreground = ErrorBrush;
        }
        finally
        {
            loginCaptchaLoadInProgress = false;
            _ = RunRefreshCooldownAsync(loginRefreshButton, ++loginRefreshGeneration, login: true);
        }
    }

    private async Task LoginAsync()
    {
        if (actionInProgress) return;
        var username = loginUsername.Text?.Trim() ?? string.Empty;
        var password = loginPassword.Text ?? string.Empty;
        var captcha = loginCaptchaAnswer.Text?.Trim() ?? string.Empty;
        if (username.Length == 0 || password.Length == 0 || captcha.Length == 0 || loginCaptchaId == Guid.Empty)
        {
            loginStatus.Text = "请输入用户名、密码和人机验证码。";
            loginStatus.Foreground = ErrorBrush;
            if (loginCaptchaId == Guid.Empty && loginRefreshButton.IsEnabled) await LoadLoginCaptchaAsync();
            return;
        }

        SetLoginActions(false);
        actionInProgress = true;
        loginStatus.Text = "正在安全登录…";
        loginStatus.Foreground = MutedBrush;
        try
        {
            using var timeout = CreateTimeout(TimeSpan.FromSeconds(20));
            await session.LoginAsync(username, password, loginCaptchaId, captcha, timeout.Token);
            loginPassword.Text = string.Empty;
            loginCaptchaAnswer.Text = string.Empty;
            loginCaptchaId = Guid.Empty;
            Complete(true);
        }
        catch (OnlineApiException exception) when (
            exception.Code == "client_update_required")
        {
            loginStatus.Text = exception.Message;
            loginStatus.Foreground = ErrorBrush;
            loginCaptchaId = Guid.Empty;
            loginCaptchaAnswer.Text = string.Empty;
            if (clientUpdateRequired != null)
                await clientUpdateRequired(exception);
        }
        catch (Exception exception) when (IsExpectedOnlineFailure(exception))
        {
            loginStatus.Text = FormatFailure(exception);
            loginStatus.Foreground = ErrorBrush;
            loginCaptchaId = Guid.Empty;
            loginCaptchaAnswer.Text = string.Empty;
        }
        finally
        {
            actionInProgress = false;
            SetLoginActions(true);
        }
        if (IsVisible && loginCaptchaId == Guid.Empty) await LoadLoginCaptchaAsync();
    }

    private async Task AuthorizeWithOsuAsync()
    {
        if (actionInProgress) return;
        actionInProgress = true;
        SetLoginActions(false);
        loginStatus.Text = "正在创建安全的 osu! 授权请求…";
        loginStatus.Foreground = MutedBrush;
        try
        {
            var user = await WindowsOsuAuthorizationCoordinator.AuthorizeAsync(
                session,
                linkCurrentAccount: false,
                message => Dispatcher.UIThread.Post(() =>
                {
                    if (disposed) return;
                    loginStatus.Text = message;
                    loginStatus.Foreground = MutedBrush;
                }),
                lifetime.Token);
            loginStatus.Text = AvaloniaLocalizationCatalog.Format("已通过 osu! 登录：{0}", user.DisplayName);
            loginStatus.Foreground = AccentBrush;
            Complete(true);
        }
        catch (Exception exception) when (IsExpectedOnlineFailure(exception))
        {
            loginStatus.Text = FormatFailure(exception);
            loginStatus.Foreground = ErrorBrush;
        }
        finally
        {
            actionInProgress = false;
            SetLoginActions(true);
        }
    }

    private async Task LoadWorkflowCaptchaAsync()
    {
        // Reached after awaits too (failed email send, cooldown end); the
        // surface may have closed in between.
        if (disposed || workflowCaptchaLoadInProgress || !workflowRefreshButton.IsEnabled) return;
        workflowCaptchaLoadInProgress = true;
        workflowCaptchaId = Guid.Empty;
        workflowCaptchaAnswer.Text = string.Empty;
        workflowRefreshButton.IsEnabled = false;
        workflowRefreshButton.Content = "载入中";
        workflowStatus.Text = "正在载入人机验证码…";
        try
        {
            using var timeout = CreateTimeout(TimeSpan.FromSeconds(8));
            var challenge = await session.GetCaptchaAsync(timeout.Token);
            // DisposeSurface already released the captcha bitmaps.
            if (disposed) return;
            workflowCaptchaId = challenge.Id;
            ReplaceBitmap(workflowCaptchaImage, ref workflowCaptchaBitmap, challenge.ImageBase64);
            workflowStatus.Text = "请输入图片中的 5 位数字，再发送邮箱验证码。";
            workflowStatus.Foreground = MutedBrush;
        }
        catch (Exception exception) when (IsExpectedOnlineFailure(exception))
        {
            workflowStatus.Text = AvaloniaLocalizationCatalog.Format("验证码载入失败：{0}", FormatFailure(exception));
            workflowStatus.Foreground = ErrorBrush;
        }
        finally
        {
            workflowCaptchaLoadInProgress = false;
            _ = RunRefreshCooldownAsync(workflowRefreshButton, ++workflowRefreshGeneration, login: false);
        }
    }

    private async Task SendEmailCodeAsync()
    {
        if (actionInProgress) return;
        var email = workflowEmail.Text?.Trim() ?? string.Empty;
        var captcha = workflowCaptchaAnswer.Text?.Trim() ?? string.Empty;
        if (email.Length == 0 || captcha.Length == 0 || workflowCaptchaId == Guid.Empty)
        {
            workflowStatus.Text = "请填写邮箱和人机验证码。";
            workflowStatus.Foreground = ErrorBrush;
            return;
        }

        actionInProgress = true;
        workflowSendCodeButton.IsEnabled = false;
        workflowStatus.Text = "正在发送邮箱验证码…";
        try
        {
            using var timeout = CreateTimeout(TimeSpan.FromSeconds(20));
            await session.SendEmailVerificationCodeAsync(
                email,
                workflowMode,
                workflowCaptchaId,
                captcha,
                timeout.Token);
            workflowStatus.Text = "验证码已发送；若未收到，请检查垃圾邮件。10 分钟内有效。";
            workflowStatus.Foreground = MutedBrush;
            _ = RunEmailCooldownAsync(++emailCooldownGeneration);
        }
        catch (OnlineApiException exception) when (
            exception.Code == "client_update_required")
        {
            workflowStatus.Text = exception.Message;
            workflowStatus.Foreground = ErrorBrush;
            if (clientUpdateRequired != null)
                await clientUpdateRequired(exception);
        }
        catch (Exception exception) when (IsExpectedOnlineFailure(exception))
        {
            workflowStatus.Text = FormatFailure(exception);
            workflowStatus.Foreground = ErrorBrush;
            workflowSendCodeButton.IsEnabled = true;
            workflowCaptchaId = Guid.Empty;
        }
        finally
        {
            actionInProgress = false;
        }
        if (workflowCaptchaId == Guid.Empty) await LoadWorkflowCaptchaAsync();
    }

    private async Task SubmitWorkflowAsync()
    {
        if (actionInProgress) return;
        var password = workflowPassword.Text ?? string.Empty;
        if (!string.Equals(password, workflowPasswordConfirm.Text, StringComparison.Ordinal))
        {
            workflowStatus.Text = "两次输入的密码不一致。";
            workflowStatus.Foreground = ErrorBrush;
            return;
        }
        if (password.Length < 10 || (workflowEmailCode.Text?.Trim().Length ?? 0) != 6)
        {
            workflowStatus.Text = "请填写 6 位邮箱验证码和至少 10 位密码。";
            workflowStatus.Foreground = ErrorBrush;
            return;
        }

        actionInProgress = true;
        workflowSubmitButton.IsEnabled = false;
        workflowStatus.Text = workflowMode == "register" ? "正在创建账号…" : "正在重置密码…";
        try
        {
            using var timeout = CreateTimeout(TimeSpan.FromSeconds(20));
            if (workflowMode == "register")
            {
                var username = workflowUsername.Text?.Trim() ?? string.Empty;
                var displayName = string.IsNullOrWhiteSpace(workflowDisplayName.Text)
                    ? username
                    : workflowDisplayName.Text.Trim();
                await session.RegisterAsync(
                    username,
                    password,
                    displayName,
                    workflowEmail.Text?.Trim() ?? string.Empty,
                    workflowEmailCode.Text?.Trim() ?? string.Empty,
                    timeout.Token);
                Complete(true);
            }
            else
            {
                await session.ResetPasswordAsync(
                    workflowEmail.Text?.Trim() ?? string.Empty,
                    workflowEmailCode.Text?.Trim() ?? string.Empty,
                    password,
                    timeout.Token);
                workflowPassword.Text = string.Empty;
                workflowPasswordConfirm.Text = string.Empty;
                ShowLogin();
                loginStatus.Text = "密码已重置，请使用新密码登录。";
                loginStatus.Foreground = MutedBrush;
                await LoadLoginCaptchaAsync();
            }
        }
        catch (OnlineApiException exception) when (
            exception.Code == "client_update_required")
        {
            workflowStatus.Text = exception.Message;
            workflowStatus.Foreground = ErrorBrush;
            if (clientUpdateRequired != null)
                await clientUpdateRequired(exception);
        }
        catch (Exception exception) when (IsExpectedOnlineFailure(exception))
        {
            workflowStatus.Text = FormatFailure(exception);
            workflowStatus.Foreground = ErrorBrush;
        }
        finally
        {
            actionInProgress = false;
            workflowSubmitButton.IsEnabled = true;
        }
    }

    private async Task LogoutAsync()
    {
        if (actionInProgress) return;
        actionInProgress = true;
        var loggedOut = false;
        try
        {
            using var timeout = CreateTimeout(TimeSpan.FromSeconds(10));
            await session.LogoutAsync(timeout.Token);
            loggedOut = true;
        }
        catch (Exception exception) when (IsExpectedOnlineFailure(exception))
        {
            identityStatus.Text = FormatFailure(exception);
            identityStatus.Foreground = ErrorBrush;
        }
        finally
        {
            actionInProgress = false;
        }
        if (!loggedOut) return;
        ShowLogin();
        loginStatus.Text = "已退出登录，本地加密会话已清除。";
        loginStatus.Foreground = MutedBrush;
        await LoadLoginCaptchaAsync();
    }

    private async Task RunRefreshCooldownAsync(Button button, int generation, bool login)
    {
        for (var seconds = 3; seconds > 0; seconds--)
        {
            if (lifetime.IsCancellationRequested ||
                generation != (login ? loginRefreshGeneration : workflowRefreshGeneration)) return;
            button.Content = AvaloniaLocalizationCatalog.Format("刷新 {0}", seconds);
            button.IsEnabled = false;
            try
            {
                await Task.Delay(1000, lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
        if (generation != (login ? loginRefreshGeneration : workflowRefreshGeneration)) return;
        button.Content = "刷新";
        button.IsEnabled = true;
    }

    private async Task RunEmailCooldownAsync(int generation)
    {
        for (var seconds = 60; seconds > 0 && generation == emailCooldownGeneration; seconds--)
        {
            workflowSendCodeButton.Content = AvaloniaLocalizationCatalog.Format("{0} 秒后重发", seconds);
            try
            {
                await Task.Delay(1000, lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
        if (generation != emailCooldownGeneration) return;
        workflowSendCodeButton.Content = "发送验证码";
        workflowSendCodeButton.IsEnabled = true;
        await LoadWorkflowCaptchaAsync();
    }

    private CancellationTokenSource CreateTimeout(TimeSpan timeout)
    {
        // A closed surface starts no new request: hand back a source that is
        // already cancelled so the caller's normal cancellation path runs.
        if (disposed)
        {
            var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            return cancelled;
        }
        var source = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        source.CancelAfter(timeout);
        return source;
    }

    private void SetLoginActions(bool enabled)
    {
        loginSubmitButton.IsEnabled = enabled;
        osuLoginButton.IsEnabled = enabled;
        registerOpenButton.IsEnabled = enabled;
        resetOpenButton.IsEnabled = enabled;
    }

    private static void ReplaceBitmap(Image image, ref Bitmap? previous, string imageBase64)
    {
        var bytes = Convert.FromBase64String(imageBase64);
        using var stream = new MemoryStream(bytes, writable: false);
        Bitmap bitmap;
        try
        {
            bitmap = new Bitmap(stream);
        }
        catch (ArgumentException exception)
        {
            // A captcha payload that is valid base64 but not a decodable image
            // is bad server data. It is reported as FormatException so the
            // online-failure filter below handles it without also swallowing
            // unrelated ArgumentExceptions.
            throw new FormatException("服务器返回了无法识别的数据，请检查网络连接后重试。", exception);
        }
        image.Source = bitmap;
        previous?.Dispose();
        previous = bitmap;
    }

    // Android's HTTP handler reports a cancelled or failed request as
    // System.Net.WebException instead of HttpRequestException or
    // OperationCanceledException, so it has to count as an expected online
    // failure here too.
    private static bool IsExpectedOnlineFailure(Exception exception) =>
        exception is OnlineApiException or HttpRequestException or System.Net.WebException or OperationCanceledException or
            System.Net.WebException or JsonException or FormatException;

    private static string FormatFailure(Exception exception) => IsCancellationFailure(exception)
        ? "连接服务器超时，请稍后重试。"
        : exception.Message;

    private static bool IsCancellationFailure(Exception exception) =>
        exception is OperationCanceledException ||
        exception is System.Net.WebException
        {
            Status: System.Net.WebExceptionStatus.RequestCanceled
        };

    private static Border BuildCaptchaRow(Image image, TextBox answer, Button refresh)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("180,*,Auto") };
        answer.Margin = new Thickness(9, 9, 6, 9);
        refresh.Margin = new Thickness(0, 9, 0, 9);
        Grid.SetColumn(answer, 1);
        Grid.SetColumn(refresh, 2);
        grid.Children.Add(image);
        grid.Children.Add(answer);
        grid.Children.Add(refresh);
        return new Border
        {
            Background = Brush("#15191B"),
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7),
            Child = grid
        };
    }

    private static Border Card(Control child, CornerRadius cornerRadius) => new()
    {
        Background = PanelBrush,
        BorderBrush = StrokeBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = cornerRadius,
        Child = child
    };

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 25,
        FontWeight = FontWeight.SemiBold,
        Foreground = Brushes.White
    };

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = MutedBrush,
        Margin = new Thickness(0, 5, 0, 0)
    };

    private static TextBlock IdentityLine(string label, string value) => new()
    {
        Text = $"{label}　{value}",
        Foreground = Brushes.White,
        FontSize = 14
    };

    private static TextBlock StatusText(string automationId)
    {
        var text = new TextBlock
        {
            Foreground = MutedBrush,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 34,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetAutomationId(text, automationId);
        return text;
    }

    private static Image CaptchaImage(string automationId)
    {
        var image = new Image
        {
            Width = 180,
            Height = 56,
            Stretch = Stretch.None
        };
        AutomationProperties.SetAutomationId(image, automationId);
        return image;
    }

    private static TextBox TextField(
        int maximumLength,
        string automationId,
        bool password = false)
    {
        TextBox text = password
            ? new WindowsPasswordTextBox()
            : new TextBox();
        text.MaxLength = maximumLength;
        text.Height = 38;
        text.Padding = new Thickness(12, 0);
        text.HorizontalAlignment = HorizontalAlignment.Stretch;
        text.VerticalContentAlignment = VerticalAlignment.Center;
        text.Background = RaisedBrush;
        text.Foreground = Brushes.White;
        text.BorderBrush = StrokeBrush;
        text.BorderThickness = new Thickness(1);
        text.CornerRadius = new CornerRadius(4);
        if (!password) WindowsImeTextInputPolicy.Apply(text);
        AutomationProperties.SetAutomationId(text, automationId);
        return text;
    }

    private static TextBox PasswordField(string automationId)
    {
        var text = TextField(128, automationId, password: true);
        text.PasswordChar = '●';
        WindowsPasswordInputPolicy.Apply(text);
        return text;
    }

    private static Button PrimaryButton(string text, string automationId)
    {
        var button = ActionButton(text, automationId);
        button.Background = AccentBrush;
        button.Foreground = Brushes.Black;
        button.BorderBrush = AccentBrush;
        return button;
    }

    private static Button OsuButton(string text, string automationId)
    {
        var button = ActionButton(text, automationId);
        button.Background = Brush("#B64E79");
        button.BorderBrush = Brush("#D86A96");
        button.Foreground = Brushes.White;
        button.FontWeight = FontWeight.SemiBold;
        return button;
    }

    private static Button LinkButton(string text, string automationId)
    {
        var button = ActionButton(text, automationId);
        button.Background = Brushes.Transparent;
        button.BorderThickness = new Thickness(0);
        button.Padding = new Thickness(7, 3);
        return button;
    }

    private static Button ActionButton(string text, string automationId)
    {
        var button = new Button
        {
            Content = text,
            Height = 38,
            Padding = new Thickness(12, 6),
            Background = RaisedBrush,
            Foreground = Brushes.White,
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetAutomationId(button, automationId);
        return button;
    }

    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));
}

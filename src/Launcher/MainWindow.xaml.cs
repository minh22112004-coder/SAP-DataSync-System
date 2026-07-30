using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SapDataSync.Launcher.Models;
using SapDataSync.Launcher.Services;
using Shape = System.Windows.Shapes.Shape;

namespace SapDataSync.Launcher;

public partial class MainWindow : Window
{
    private static readonly Brush HealthyBrush = BrushFrom("#22C55E");
    private static readonly Brush StartingBrush = BrushFrom("#F59E0B");
    private static readonly Brush StoppedBrush = BrushFrom("#EF4444");
    private static readonly Brush UnknownBrush = BrushFrom("#94A3B8");

    private readonly CancellationTokenSource shutdownTokenSource = new();
    private readonly DispatcherTimer refreshTimer;
    private readonly DockerComposeService? dockerService;
    private bool isBusy;
    private bool isWebReady;

    public MainWindow()
    {
        InitializeComponent();

        var location = ProjectLocator.Find();
        if (location is not null)
        {
            dockerService = new DockerComposeService(location, new CommandRunner());
            ProjectPathText.Text = $"Cấu hình: {location.ComposeFile}";
        }
        else
        {
            ProjectPathText.Text = "Không tìm thấy compose.yaml. Hãy đặt Launcher trong thư mục dự án.";
            AppendLog("Không tìm thấy compose.yaml. Có thể đặt biến SAPDATASYNC_ROOT tới thư mục hệ thống.");
        }

        refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        refreshTimer.Tick += RefreshTimer_Tick;
        refreshTimer.Start();

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e) =>
        await RefreshSnapshotAsync(showLog: false);

    private async void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (!isBusy)
        {
            await RefreshSnapshotAsync(showLog: false);
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginOperation("Đang chuẩn bị khởi động hệ thống…"))
        {
            return;
        }

        try
        {
            if (dockerService is null)
            {
                ShowConfigurationError();
                return;
            }

            var snapshot = await dockerService.GetSnapshotAsync(shutdownTokenSource.Token);
            ApplySnapshot(snapshot);

            if (!snapshot.DockerRunning)
            {
                if (!snapshot.DockerAvailable)
                {
                    MessageBox.Show(
                        "Chưa tìm thấy Docker Desktop trên máy. Hãy cài Docker Desktop rồi thử lại.",
                        "Thiếu Docker Desktop",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    AppendLog("Không thể khởi động: chưa cài Docker Desktop.");
                    return;
                }

                AppendLog("Docker Desktop chưa chạy. Launcher đang mở Docker Desktop…");
                if (!DockerComposeService.TryStartDockerDesktop())
                {
                    MessageBox.Show(
                        "Không thể tự mở Docker Desktop. Hãy mở Docker Desktop, đợi ứng dụng sẵn sàng rồi bấm Khởi động lại.",
                        "Docker chưa sẵn sàng",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                if (!await WaitForDockerAsync(TimeSpan.FromSeconds(90)))
                {
                    MessageBox.Show(
                        "Docker Desktop mất nhiều thời gian để khởi động. Hãy đợi Docker báo sẵn sàng rồi thử lại.",
                        "Docker chưa sẵn sàng",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }
            }

            var setupResult = EnvironmentSetupService.EnsureConfigured(dockerService.Location.RootDirectory);
            if (setupResult.Created)
            {
                AppendLog(setupResult.Message);
            }

            var hasContainers = await dockerService.HasExistingContainersAsync(shutdownTokenSource.Token);
            AppendLog(hasContainers
                ? "Đang khởi động các dịch vụ…"
                : "Lần chạy đầu tiên: đang tạo ứng dụng và khởi động các dịch vụ…");

            var result = await dockerService.StartAsync(
                buildImages: !hasContainers,
                shutdownTokenSource.Token);

            if (!result.Succeeded)
            {
                AppendLog($"Khởi động thất bại: {FriendlyError(result)}");
                MessageBox.Show(
                    FriendlyError(result),
                    "Không thể khởi động hệ thống",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            AppendLog("Các dịch vụ đã được tạo. Đang chờ kiểm tra sức khỏe hệ thống…");
            var ready = await dockerService.WaitUntilReadyAsync(
                TimeSpan.FromMinutes(2),
                snapshotResult => Dispatcher.Invoke(() => ApplySnapshot(snapshotResult)),
                shutdownTokenSource.Token);

            if (ready)
            {
                AppendLog("Hệ thống đã khởi động thành công và sẵn sàng sử dụng.");
            }
            else
            {
                AppendLog("Hệ thống đã chạy nhưng một số dịch vụ chưa báo ổn định. Hãy bấm Làm mới hoặc kiểm tra log Docker.");
                MessageBox.Show(
                    "Hệ thống đã được khởi động nhưng chưa sẵn sàng hoàn toàn. Vui lòng đợi thêm rồi bấm Làm mới trạng thái.",
                    "Đang hoàn tất khởi động",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException) when (shutdownTokenSource.IsCancellationRequested)
        {
            // The application is closing.
        }
        catch (Exception exception)
        {
            HandleUnexpectedError(exception);
        }
        finally
        {
            EndOperation();
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginOperation("Đang dừng hệ thống an toàn…"))
        {
            return;
        }

        try
        {
            if (dockerService is null)
            {
                ShowConfigurationError();
                return;
            }

            var result = await dockerService.StopAsync(shutdownTokenSource.Token);
            if (!result.Succeeded)
            {
                AppendLog($"Dừng hệ thống thất bại: {FriendlyError(result)}");
                MessageBox.Show(
                    FriendlyError(result),
                    "Không thể dừng hệ thống",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            AppendLog("Hệ thống đã dừng. Database và các file dữ liệu vẫn được giữ nguyên.");
            await RefreshSnapshotAsync(showLog: false);
        }
        catch (OperationCanceledException) when (shutdownTokenSource.IsCancellationRequested)
        {
            // The application is closing.
        }
        catch (Exception exception)
        {
            HandleUnexpectedError(exception);
        }
        finally
        {
            EndOperation();
        }
    }

    private void OpenWebButton_Click(object sender, RoutedEventArgs e)
    {
        if (dockerService is null)
        {
            ShowConfigurationError();
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = dockerService.Location.WebUri.AbsoluteUri,
                UseShellExecute = true
            });
            AppendLog($"Đã mở Web App: {dockerService.Location.WebUri}");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(
                $"Không thể mở trình duyệt. Bạn có thể truy cập: {dockerService.Location.WebUri}",
                "Không thể mở Web App",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginOperation("Đang cập nhật trạng thái…"))
        {
            return;
        }

        try
        {
            await RefreshSnapshotAsync(showLog: true);
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task RefreshSnapshotAsync(bool showLog)
    {
        if (dockerService is null || shutdownTokenSource.IsCancellationRequested)
        {
            ApplyUnavailableState();
            return;
        }

        try
        {
            var snapshot = await dockerService.GetSnapshotAsync(shutdownTokenSource.Token);
            ApplySnapshot(snapshot);
            if (showLog)
            {
                AppendLog(snapshot.DockerDetail);
            }
        }
        catch (OperationCanceledException) when (shutdownTokenSource.IsCancellationRequested)
        {
            // The application is closing.
        }
        catch (Exception exception)
        {
            ApplyUnavailableState();
            if (showLog)
            {
                AppendLog($"Không đọc được trạng thái: {exception.Message}");
            }
        }
    }

    private async Task<bool> WaitForDockerAsync(TimeSpan timeout)
    {
        if (dockerService is null)
        {
            return false;
        }

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = await dockerService.GetSnapshotAsync(shutdownTokenSource.Token);
            ApplySnapshot(snapshot);
            if (snapshot.DockerRunning)
            {
                AppendLog("Docker Desktop đã sẵn sàng.");
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), shutdownTokenSource.Token);
        }

        return false;
    }

    private void ApplySnapshot(SystemSnapshot snapshot)
    {
        ApplyService(snapshot.Services, "sqlserver", SqlStatusDot, SqlStatusText);
        ApplyService(snapshot.Services, "web-api", WebStatusDot, WebStatusText);
        ApplyService(snapshot.Services, "etl-worker", EtlStatusDot, EtlStatusText);

        if (!snapshot.DockerRunning)
        {
            OverallStatusText.Text = snapshot.DockerAvailable ? "Docker chưa chạy" : "Chưa cài Docker";
            OverallStatusDot.Fill = StoppedBrush;
            OverallStatusBadge.Background = BrushFrom("#7F1D1D");
        }
        else if (snapshot.IsHealthy)
        {
            OverallStatusText.Text = "Hệ thống ổn định";
            OverallStatusDot.Fill = HealthyBrush;
            OverallStatusBadge.Background = BrushFrom("#14532D");
        }
        else if (snapshot.Services.Values.Any(service => service.State is ServiceState.Running or ServiceState.Starting or ServiceState.Healthy))
        {
            OverallStatusText.Text = "Đang khởi động";
            OverallStatusDot.Fill = StartingBrush;
            OverallStatusBadge.Background = BrushFrom("#78350F");
        }
        else
        {
            OverallStatusText.Text = "Hệ thống đã dừng";
            OverallStatusDot.Fill = StoppedBrush;
            OverallStatusBadge.Background = BrushFrom("#7F1D1D");
        }

        isWebReady = snapshot.Services.TryGetValue("web-api", out var web) &&
            web.State is ServiceState.Healthy or ServiceState.Running;
        OpenWebButton.IsEnabled = !isBusy && isWebReady;
        LastUpdatedText.Text = $"Cập nhật {DateTime.Now:HH:mm:ss}";
    }

    private static void ApplyService(
        IReadOnlyDictionary<string, ServiceStatus> services,
        string serviceName,
        Shape statusDot,
        System.Windows.Controls.TextBlock statusText)
    {
        if (!services.TryGetValue(serviceName, out var service))
        {
            statusDot.Fill = UnknownBrush;
            statusText.Text = "Chưa xác định";
            return;
        }

        statusText.Text = service.Detail;
        statusDot.Fill = service.State switch
        {
            ServiceState.Healthy or ServiceState.Running => HealthyBrush,
            ServiceState.Starting => StartingBrush,
            ServiceState.Stopped or ServiceState.Unhealthy => StoppedBrush,
            _ => UnknownBrush
        };
    }

    private void ApplyUnavailableState()
    {
        var services = new Dictionary<string, ServiceStatus>
        {
            ["sqlserver"] = new("sqlserver", ServiceState.Unknown, "Chưa xác định"),
            ["web-api"] = new("web-api", ServiceState.Unknown, "Chưa xác định"),
            ["etl-worker"] = new("etl-worker", ServiceState.Unknown, "Chưa xác định")
        };
        ApplySnapshot(new SystemSnapshot(false, false, "Không tìm thấy cấu hình.", services));
    }

    private bool TryBeginOperation(string message)
    {
        if (isBusy)
        {
            return false;
        }

        isBusy = true;
        SetButtonsEnabled(false);
        BusyProgress.Visibility = Visibility.Visible;
        AppendLog(message);
        return true;
    }

    private void EndOperation()
    {
        isBusy = false;
        SetButtonsEnabled(true);
        BusyProgress.Visibility = Visibility.Collapsed;
    }

    private void SetButtonsEnabled(bool enabled)
    {
        StartButton.IsEnabled = enabled;
        StopButton.IsEnabled = enabled;
        RefreshButton.IsEnabled = enabled;
        OpenWebButton.IsEnabled = enabled && isWebReady;
    }

    private void AppendLog(string message)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
        ActivityLog.Text = string.IsNullOrWhiteSpace(ActivityLog.Text)
            ? entry
            : $"{ActivityLog.Text}{Environment.NewLine}{entry}";
        ActivityLog.ScrollToEnd();
    }

    private static string FriendlyError(CommandResult result)
    {
        var message = result.ErrorMessage;
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Docker không trả về thông tin lỗi. Vui lòng kiểm tra Docker Desktop.";
        }

        if (message.Contains("MSSQL_SA_PASSWORD", StringComparison.OrdinalIgnoreCase))
        {
            return "Mật khẩu cơ sở dữ liệu chưa được cấu hình. Vui lòng hoàn tất thiết lập lần đầu.";
        }

        return message.Length > 900 ? $"{message[..900]}…" : message;
    }

    private void ShowConfigurationError() =>
        MessageBox.Show(
            "Không tìm thấy compose.yaml. Hãy đặt Launcher trong thư mục hệ thống hoặc cấu hình SAPDATASYNC_ROOT.",
            "Không tìm thấy hệ thống",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

    private void HandleUnexpectedError(Exception exception)
    {
        AppendLog($"Đã xảy ra lỗi: {exception.Message}");
        MessageBox.Show(
            "Đã xảy ra lỗi ngoài dự kiến. Chi tiết đã được hiển thị trong phần Thông báo.",
            "Lỗi Launcher",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        refreshTimer.Stop();
        shutdownTokenSource.Cancel();
        shutdownTokenSource.Dispose();
    }

    private static SolidColorBrush BrushFrom(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}

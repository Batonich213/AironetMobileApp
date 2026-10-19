using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace AironetAndroidApp;

public partial class MainPage : ContentPage
{
    private const string LoginUrl = "https://bill.aironet.ru/bill/aaa5";
    private const string UserPageUrl = "https://bill.aironet.ru/bill/user5";
    private const string PaymentUrl = "https://aironet.ru/oplata/";
    private const string LoginStorageKey = "UserLogin";
    private const string PasswordStorageKey = "UserPassword";

    public MainPage()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        LoginButton.IsEnabled = true;
        await LoadSavedCredentialsAsync();
    }

    private async Task LoadSavedCredentialsAsync()
    {
        try
        {
            string? savedLogin = await SecureStorage.Default.GetAsync(LoginStorageKey);
            string? savedPassword = await SecureStorage.Default.GetAsync(PasswordStorageKey);

            if (!string.IsNullOrWhiteSpace(savedLogin) && !string.IsNullOrWhiteSpace(savedPassword))
            {
                LoginEntry.Text = savedLogin;
                PasswordEntry.Text = savedPassword;
                RememberMeCheckBox.IsChecked = true;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Ошибка чтения SecureStorage: {ex.Message}");
        }
    }

    private async void OnLoginButtonClicked(object? sender, EventArgs e)
    {
        string login = LoginEntry.Text?.Trim() ?? string.Empty;
        string password = PasswordEntry.Text ?? string.Empty;

        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(password))
        {
            await DisplayAlertAsync("Ошибка", "Введите логин и пароль", "ОК");
            return;
        }

        await SaveCredentialsAsync(login, password);
        SetLoadingState(true);

        try
        {
            using HttpClient client = CreateHttpClient();
            string loginHtml = await SendLoginRequestAsync(client, login, password);

            if (!IsLoginSuccessful(loginHtml))
            {
                await DisplayAlertAsync("Ошибка входа", "Неверный логин или пароль.", "ОК");
                return;
            }

            string sessionKey = ExtractSessionKey(loginHtml);
            if (string.IsNullOrEmpty(sessionKey))
            {
                await DisplayAlertAsync("Ошибка", "Не удалось получить ключ сессии (skey).", "ОК");
                return;
            }

            DashboardData dashboardData = await LoadDashboardDataAsync(client, sessionKey);
            DisplayDashboard(dashboardData);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Ошибка сети", ex.Message, "ОК");
        }
        finally
        {
            SetLoadingState(false);
        }
    }

    private async Task SaveCredentialsAsync(string login, string password)
    {
        try
        {
            if (RememberMeCheckBox.IsChecked)
            {
                await SecureStorage.Default.SetAsync(LoginStorageKey, login);
                await SecureStorage.Default.SetAsync(PasswordStorageKey, password);
                return;
            }

            SecureStorage.Default.Remove(LoginStorageKey);
            SecureStorage.Default.Remove(PasswordStorageKey);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Ошибка записи SecureStorage: {ex.Message}");
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer()
        };

        return new HttpClient(handler);
    }

    private static async Task<string> SendLoginRequestAsync(HttpClient client, string login, string password)
    {
        var formData = new Dictionary<string, string>
        {
            ["login"] = login,
            ["password"] = password,
            ["cmd"] = "login"
        };

        using var content = new FormUrlEncodedContent(formData);
        using HttpResponseMessage response = await client.PostAsync(LoginUrl, content);
        return await response.Content.ReadAsStringAsync();
    }

    private static bool IsLoginSuccessful(string html) =>
        html.Contains("Общая информация", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("user5", StringComparison.OrdinalIgnoreCase);

    private static async Task<DashboardData> LoadDashboardDataAsync(HttpClient client, string sessionKey)
    {
        string reportsUrl = $"{UserPageUrl}?cmd=user_reports&skey={sessionKey}";
        string reportsHtml = await client.GetStringAsync(reportsUrl);

        string servicesUrl = $"{UserPageUrl}?cmd=user_services_list&skey={sessionKey}";
        string servicesHtml = await client.GetStringAsync(servicesUrl);

        ParseTariff(servicesHtml, out string tariffName, out string tariffPrice, out string tariffDate);

        return new DashboardData(
            GetValueOrDefault(reportsHtml, "Баланс основного счета", "0.00"),
            GetValueOrDefault(reportsHtml, "Полное имя", "Абонент"),
            GetValueOrDefault(reportsHtml, "Блокировка", "Неизвестно"),
            tariffName,
            tariffPrice,
            tariffDate);
    }

    private void DisplayDashboard(DashboardData data)
    {
        LoginLayout.IsVisible = false;
        DashboardLayout.IsVisible = true;
        BalanceLabel.Text = $"{data.Balance} ₽";
        UserNameLabel.Text = data.FullName;
        BlockStatusLabel.Text = data.BlockStatus;
        TariffNameLabel.Text = data.TariffName;
        TariffPriceLabel.Text = $"{data.TariffPrice} ₽ / мес";
        TariffDateLabel.Text = $"Списание: {data.TariffDate}";

        Color statusColor = data.BlockStatus.Contains("заблокирован", StringComparison.OrdinalIgnoreCase)
            ? Colors.Red
            : Color.FromArgb("#28a745");

        BlockStatusLabel.TextColor = statusColor;
        StatusBorder.Stroke = statusColor;
    }

    private static string GetValueOrDefault(string html, string parameterName, string defaultValue)
    {
        string value = ExtractValueFromHtml(html, parameterName);
        return string.IsNullOrEmpty(value) ? defaultValue : value;
    }

    private static string ExtractSessionKey(string html)
    {
        var match = Regex.Match(html, @"skey=([a-f0-9]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static string ExtractValueFromHtml(string html, string parameterName)
    {
        int index = html.IndexOf(parameterName, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return string.Empty;
        }

        int length = Math.Min(500, html.Length - index);
        string subString = html.Substring(index, length);

        int tdStart = subString.IndexOf("<td", StringComparison.OrdinalIgnoreCase);
        if (tdStart < 0)
        {
            return string.Empty;
        }

        int tdTagEnd = subString.IndexOf(">", tdStart);
        if (tdTagEnd < 0)
        {
            return string.Empty;
        }

        int tdClose = subString.IndexOf("</td>", tdTagEnd, StringComparison.OrdinalIgnoreCase);
        if (tdClose < 0)
        {
            return string.Empty;
        }

        string value = subString.Substring(tdTagEnd + 1, tdClose - tdTagEnd - 1);
        value = value.Replace("\r", string.Empty).Replace("\n", string.Empty).Replace("&nbsp;", " ").Trim();
        return Regex.Replace(value, "<.*?>", string.Empty).Trim();
    }

    private static void ParseTariff(string html, out string tariffName, out string price, out string date)
    {
        tariffName = "Не найден";
        price = "0";
        date = "Неизвестно";

        int index = html.IndexOf("Periodic service", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return;
        }

        int length = Math.Min(2000, html.Length - index);
        string subString = html.Substring(index, length);

        string[] parts = Regex.Split(subString, @"</td>", RegexOptions.IgnoreCase)
            .Select(part => Regex.Replace(part, @"<.*?>", string.Empty)
                .Replace("\r", string.Empty)
                .Replace("\n", " ")
                .Trim())
            .ToArray();

        if (parts.Length > 6)
        {
            foreach (string part in parts)
            {
                if (part.Contains("порт", StringComparison.OrdinalIgnoreCase) ||
                    part.Contains("Мбит", StringComparison.OrdinalIgnoreCase) ||
                    part.Contains("Гбит", StringComparison.OrdinalIgnoreCase))
                {
                    tariffName = part;
                    break;
                }
            }

            for (int i = parts.Length - 1; i >= 0; i--)
            {
                if (decimal.TryParse(parts[i], NumberStyles.Number, CultureInfo.InvariantCulture, out _))
                {
                    price = parts[i];
                    break;
                }
            }

            foreach (string part in parts)
            {
                if (ContainsDayOfWeek(part))
                {
                    int dashIndex = part.IndexOf('-');
                    if (dashIndex >= 0)
                    {
                        string endDateStr = part.Substring(dashIndex + 1).Trim();
                        date = FormatDateString(endDateStr);
                    }
                    else
                    {
                        date = part;
                    }
                    break;
                }
            }
        }
    }

    private static bool ContainsDayOfWeek(string value) =>
        value.Contains("Sun", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Mon", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Tue", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Wed", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Thu", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Fri", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Sat", StringComparison.OrdinalIgnoreCase);

    private static string FormatDateString(string dateStr)
    {
        dateStr = Regex.Replace(dateStr, @"\s+", " ");

        string[] formats = { "ddd MMM d HH:mm:ss yyyy", "ddd MMM dd HH:mm:ss yyyy" };

        if (DateTime.TryParseExact(dateStr, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
        {
            return date.ToString("dd.MM HH:mm:ss", CultureInfo.InvariantCulture);
        }

        if (DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return date.ToString("dd.MM HH:mm:ss", CultureInfo.InvariantCulture);
        }

        return dateStr;
    }

    private void SetLoadingState(bool isLoading)
    {
        LoadingIndicator.IsVisible = isLoading;
        LoadingIndicator.IsRunning = isLoading;
        LoginButton.IsEnabled = !isLoading;
    }

    private async void OnPayButtonClicked(object? sender, EventArgs e)
    {
        try
        {
            await Launcher.Default.OpenAsync(PaymentUrl);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Ошибка", $"Не удалось открыть страницу оплаты: {ex.Message}", "ОК");
        }
    }

    private void OnLogoutButtonClicked(object? sender, EventArgs e)
    {
        DashboardLayout.IsVisible = false;
        LoginLayout.IsVisible = true;
        LoginButton.IsEnabled = true;
    }

    private sealed record DashboardData(
        string Balance,
        string FullName,
        string BlockStatus,
        string TariffName,
        string TariffPrice,
        string TariffDate);
}
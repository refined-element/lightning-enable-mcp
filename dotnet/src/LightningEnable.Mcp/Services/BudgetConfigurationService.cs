using System.Text.Json;
using LightningEnable.Mcp.Models;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Service for loading and managing user budget configuration.
/// Configuration is READ-ONLY at runtime - no tool can modify it.
/// </summary>
public interface IBudgetConfigurationService
{
    /// <summary>
    /// Gets the user's budget configuration.
    /// </summary>
    UserBudgetConfiguration Configuration { get; }

    /// <summary>
    /// Gets the path to the configuration file.
    /// </summary>
    string ConfigFilePath { get; }

    /// <summary>
    /// Whether the configuration was loaded from an existing file.
    /// </summary>
    bool ConfigFileExists { get; }

    /// <summary>
    /// Reloads configuration from disk.
    /// </summary>
    void Reload();
}

/// <summary>
/// Loads budget configuration from ~/.lightning-enable/config.json.
/// This file is user-editable but NOT modifiable by AI agents.
/// </summary>
public class BudgetConfigurationService : IBudgetConfigurationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _configDirectory;
    private readonly string _configFilePath;
    private UserBudgetConfiguration _configuration;
    private bool _configFileExists;

    public BudgetConfigurationService()
    {
        // Determine config directory based on platform
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _configDirectory = Path.Combine(homeDir, ".lightning-enable");
        _configFilePath = Path.Combine(_configDirectory, "config.json");

        _configuration = LoadConfiguration();
    }

    public UserBudgetConfiguration Configuration => _configuration;
    public string ConfigFilePath => _configFilePath;
    public bool ConfigFileExists => _configFileExists;

    public void Reload()
    {
        _configuration = LoadConfiguration();
    }

    private UserBudgetConfiguration LoadConfiguration()
    {
        try
        {
            if (File.Exists(_configFilePath))
            {
                _configFileExists = true;
                var json = File.ReadAllText(_configFilePath);
                var config = JsonSerializer.Deserialize<UserBudgetConfiguration>(json, JsonOptions);

                if (config != null)
                {
                    ValidateConfiguration(config);
                    LogConfigLoaded(config);
                    return config;
                }
            }
            else
            {
                _configFileExists = false;
                CreateDefaultConfigFile();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Lightning Enable] Warning: Could not load config from {_configFilePath}: {ex.Message}");
            Console.Error.WriteLine("[Lightning Enable] Using default configuration.");
        }

        return CreateDefaultConfiguration();
    }

    private void CreateDefaultConfigFile()
    {
        try
        {
            // Create directory if it doesn't exist
            if (!Directory.Exists(_configDirectory))
            {
                Directory.CreateDirectory(_configDirectory);
            }

            var defaultConfig = CreateDefaultConfiguration();
            var json = JsonSerializer.Serialize(defaultConfig, JsonOptions);
            File.WriteAllText(_configFilePath, json);

            Console.Error.WriteLine();
            Console.Error.WriteLine("╔══════════════════════════════════════════════════════════════════╗");
            Console.Error.WriteLine("║          Lightning Enable MCP - First Run Setup                  ║");
            Console.Error.WriteLine("╠══════════════════════════════════════════════════════════════════╣");
            Console.Error.WriteLine("║  A default configuration file has been created at:               ║");
            Console.Error.WriteLine($"║  {_configFilePath,-60} ║");
            Console.Error.WriteLine("║                                                                  ║");
            Console.Error.WriteLine("║  Default spending limits (in USD):                               ║");
            Console.Error.WriteLine("║    - Auto-approve:      <= $0.10                                 ║");
            Console.Error.WriteLine("║    - Log & approve:     $0.10 - $1.00                            ║");
            Console.Error.WriteLine("║    - Require confirm:   $1.00 - $10.00                           ║");
            Console.Error.WriteLine("║    - Browser confirm:   $10.00 - $100.00                         ║");
            Console.Error.WriteLine("║    - Max per payment:   $500.00                                  ║");
            Console.Error.WriteLine("║    - Max per session:   $100.00                                  ║");
            Console.Error.WriteLine("║                                                                  ║");
            Console.Error.WriteLine("║  Edit the config file to customize these limits.                 ║");
            Console.Error.WriteLine("║  AI agents CANNOT modify this file.                              ║");
            Console.Error.WriteLine("╚══════════════════════════════════════════════════════════════════╝");
            Console.Error.WriteLine();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Lightning Enable] Could not create default config: {ex.Message}");
        }
    }

    private static UserBudgetConfiguration CreateDefaultConfiguration()
    {
        return new UserBudgetConfiguration
        {
            Currency = "USD",
            Tiers = new TierThresholds
            {
                AutoApprove = 0.10m,
                LogAndApprove = 1.00m,
                FormConfirm = 10.00m,
                UrlConfirm = 100.00m
            },
            Limits = new PaymentLimits
            {
                MaxPerPayment = 500.00m,
                MaxPerSession = 100.00m
            },
            Session = new SessionSettings
            {
                RequireApprovalForFirstPayment = true,
                CooldownSeconds = 2
            }
        };
    }

    private static void ValidateConfiguration(UserBudgetConfiguration config)
    {
        // Ensure tiers are in ascending order
        if (config.Tiers.AutoApprove > config.Tiers.LogAndApprove)
        {
            Console.Error.WriteLine("[Lightning Enable] Warning: autoApprove should be <= logAndApprove. Using defaults.");
            config.Tiers = new TierThresholds();
        }

        if (config.Tiers.LogAndApprove > config.Tiers.FormConfirm)
        {
            Console.Error.WriteLine("[Lightning Enable] Warning: logAndApprove should be <= formConfirm. Using defaults.");
            config.Tiers = new TierThresholds();
        }

        if (config.Tiers.FormConfirm > config.Tiers.UrlConfirm)
        {
            Console.Error.WriteLine("[Lightning Enable] Warning: formConfirm should be <= urlConfirm. Using defaults.");
            config.Tiers = new TierThresholds();
        }

        // Ensure limits are positive
        if (config.Limits.MaxPerPayment.HasValue && config.Limits.MaxPerPayment.Value <= 0)
        {
            Console.Error.WriteLine("[Lightning Enable] Warning: maxPerPayment must be positive. Using default.");
            config.Limits.MaxPerPayment = 500.00m;
        }

        if (config.Limits.MaxPerSession.HasValue && config.Limits.MaxPerSession.Value <= 0)
        {
            Console.Error.WriteLine("[Lightning Enable] Warning: maxPerSession must be positive. Using default.");
            config.Limits.MaxPerSession = 100.00m;
        }

        // Ensure cooldown is reasonable
        if (config.Session.CooldownSeconds < 0 || config.Session.CooldownSeconds > 60)
        {
            Console.Error.WriteLine("[Lightning Enable] Warning: cooldownSeconds should be 0-60. Using default.");
            config.Session.CooldownSeconds = 2;
        }
    }

    private void LogConfigLoaded(UserBudgetConfiguration config)
    {
        Console.Error.WriteLine($"[Lightning Enable] Loaded budget config from {_configFilePath}");
        Console.Error.WriteLine($"[Lightning Enable] Tiers: auto<=${config.Tiers.AutoApprove:C}, log<=${config.Tiers.LogAndApprove:C}, form<=${config.Tiers.FormConfirm:C}, url<=${config.Tiers.UrlConfirm:C}");

        var maxPayment = config.Limits.MaxPerPayment.HasValue
            ? config.Limits.MaxPerPayment.Value.ToString("C")
            : "unlimited";
        var maxSession = config.Limits.MaxPerSession.HasValue
            ? config.Limits.MaxPerSession.Value.ToString("C")
            : "unlimited";
        Console.Error.WriteLine($"[Lightning Enable] Limits: max/payment={maxPayment}, max/session={maxSession}");
    }
}

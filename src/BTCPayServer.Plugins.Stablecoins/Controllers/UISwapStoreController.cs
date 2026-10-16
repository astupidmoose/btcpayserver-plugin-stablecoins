#nullable enable
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Stablecoins.Swap;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Stablecoins.Controllers;

[Route("stores/{storeId}/stablecoins/swap")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class UISwapStoreController(StoreRepository storeRepository, PaymentMethodHandlerDictionary handlers,
    ILogger<UISwapStoreController> logger) : Controller
{
    public const string SettingsView = StablecoinsPlugin.ViewsDirectory + "/SwapSettings.cshtml";
    /// <summary>The settings themselves, kept apart from the payment methods so turning every chain off keeps them.</summary>
    public const string StoreSettingName = "Stablecoins.SwapSettings";

    StoreData StoreData => HttpContext.GetStoreData();

    [HttpGet("")]
    public async Task<IActionResult> SwapSettings()
    {
        var store = StoreData;
        var excluded = store.GetStoreBlob().GetExcludedPaymentMethods();
        var configs = SwapSourceChain.PaymentMethods
            .Select(c => (Chain: c, Config: store.GetPaymentMethodConfig<SwapToBtcPaymentMethodConfig>(c.PaymentMethodId, handlers)))
            .ToList();
        var any = await storeRepository.GetSettingAsync<SwapToBtcPaymentMethodConfig>(store.Id, StoreSettingName) ??
                  configs.Select(c => c.Config).FirstOrDefault(c => c is not null) ?? new SwapToBtcPaymentMethodConfig();
        // While the feature is off the chains have no payment method, so their toggles come from the remembered choice.
        var chains = configs.Select(c => (c.Chain, Enabled: any.Disabled
                ? any.Chains.Contains(c.Chain.Code, System.StringComparer.OrdinalIgnoreCase)
                : c.Config is not null && !excluded.Match(c.Chain.PaymentMethodId)))
            .Concat(SwapSourceChain.ExchangeOptions.Select(c => (Chain: c, Enabled: any.ExchangeOptions.Contains(c.Code))));
        return View(SettingsView, new SwapSettingsViewModel
        {
            HasBtcWallet = store.GetDerivationSchemeSettings(handlers, "BTC") is not null,
            Enabled = !any.Disabled,
            Integrator = any.Integrator ?? SwapDefaults.Integrator,
            ApiKey = any.ApiKey,
            FeePercent = any.Fee * 100m,
            BufferPercent = any.BufferPercent,
            WalletConnectProjectId = any.WalletConnectProjectId,
            IntentsApiKey = any.IntentsApiKey,
            RefundEvm = any.RefundEvm,
            RefundSolana = any.RefundSolana,
            RefundTron = any.RefundTron,
            IntentsSlippageBps = any.IntentsSlippageBps,
            IntentsUrl = any.IntentsUrl,
            Chains = chains.Select(c => new SwapChainViewModel
            {
                Code = c.Chain.Code,
                Name = c.Chain.Name,
                PaymentMethodId = c.Chain.PaymentMethodId.ToString(),
                Image = c.Chain.Image,
                Provider = c.Chain.Provider,
                Enabled = c.Enabled
            }).ToList()
        });
    }

    [HttpPost("")]
    public async Task<IActionResult> SwapSettings(SwapSettingsViewModel vm)
    {
        var store = StoreData;
        logger.LogInformation("Saving swap settings of store {StoreId}", store.Id);
        vm.HasBtcWallet = store.GetDerivationSchemeSettings(handlers, "BTC") is not null;
        var config = new SwapToBtcPaymentMethodConfig
        {
            Integrator = vm.Integrator?.Trim(),
            ApiKey = string.IsNullOrWhiteSpace(vm.ApiKey) ? null : vm.ApiKey.Trim(),
            Fee = vm.FeePercent / 100m,
            BufferPercent = vm.BufferPercent,
            WalletConnectProjectId = string.IsNullOrWhiteSpace(vm.WalletConnectProjectId) ? null : vm.WalletConnectProjectId.Trim(),
            IntentsApiKey = Clean(vm.IntentsApiKey),
            IntentsUrl = Clean(vm.IntentsUrl),
            RefundEvm = Clean(vm.RefundEvm),
            RefundSolana = Clean(vm.RefundSolana),
            RefundTron = Clean(vm.RefundTron),
            IntentsSlippageBps = vm.IntentsSlippageBps
        };
        foreach (var (field, message) in SwapToBtcPaymentMethodHandler.Validate(config))
            ModelState.AddModelError(field == nameof(config.Fee) ? nameof(vm.FeePercent) : field, message);
        if (config.RefundEvm is { } evm && !IsEvmAddress(evm))
            ModelState.AddModelError(nameof(vm.RefundEvm), "The EVM refund address should be 0x followed by 40 hex characters.");
        if (config.RefundSolana is { } sol && !IsBase58(sol, 32, 44))
            ModelState.AddModelError(nameof(vm.RefundSolana), "That does not look like a Solana address.");
        if (config.RefundTron is { } tron && !(tron.StartsWith('T') && IsBase58(tron, 34, 34)))
            ModelState.AddModelError(nameof(vm.RefundTron), "A Tron address starts with T and has 34 characters.");
        var enabled = vm.Chains.Where(c => c.Enabled).Select(c => SwapSourceChain.Find(c.Code)).OfType<SwapSourceChain>().ToHashSet();
        var exchange = enabled.Where(c => c.Provider == SwapProvider.Intents).ToList();
        config.ExchangeOptions = exchange.Select(c => c.Code).ToArray();
        config.Chains = enabled.Where(c => c.Provider == SwapProvider.Lifi).Select(c => c.Code).ToArray();
        config.Disabled = !vm.Enabled;
        foreach (var chain in exchange)
            if (!Intents.IntentsDepositService.Networks(chain, config).Any())
                ModelState.AddModelError(nameof(vm.RefundEvm), $"Add at least one refund address to offer {chain.Name}: NEAR Intents returns payments it cannot swap there.");
        if (exchange.Count > 0 && !enabled.Any(c => c.Provider == SwapProvider.Lifi))
            ModelState.AddModelError(string.Empty, "USDC and USDT from an exchange are offered on the token checkouts: turn on at least one token chain (Ethereum, Base...).");
        if (vm.Enabled && enabled.Count > 0 && !vm.HasBtcWallet)
            ModelState.AddModelError(string.Empty, "Set up the store's BTC on-chain wallet first: swapped BTC is paid into it.");
        if (!ModelState.IsValid)
        {
            var errors = string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).Distinct());
            logger.LogWarning("Swap settings of store {StoreId} not saved: {Errors}", store.Id, errors);
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Severity = StatusMessageModel.StatusSeverity.Error,
                Message = "Not saved: " + errors
            });
            FillChains(vm);
            return View(SettingsView, vm);
        }

        var blob = store.GetStoreBlob();
        foreach (var chain in SwapSourceChain.PaymentMethods)
        {
            var handler = handlers[chain.PaymentMethodId];
            var on = vm.Enabled && enabled.Contains(chain);
            // Disabled chains are removed, not just excluded, so WooCommerce stops listing them.
            store.SetPaymentMethodConfig(handler, on ? config : null);
            blob.SetExcluded(chain.PaymentMethodId, !on);
        }
        store.SetStoreBlob(blob);
        await storeRepository.UpdateStore(store);
        await storeRepository.UpdateSetting(store.Id, StoreSettingName, config);
        logger.LogInformation("Swap settings of store {StoreId} saved, {Count} chains enabled", store.Id, enabled.Count);

        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = StatusMessageModel.StatusSeverity.Success,
            Message = !vm.Enabled
                ? "Swap settings saved. Paying from other chains is turned off."
                : enabled.Count == 0
                ? "Swap settings saved. No chains are enabled."
                : $"Swap settings saved. Customers can pay from {string.Join(", ", SwapSourceChain.All.Where(enabled.Contains).Select(c => c.Name))}."
        });
        return RedirectToAction(nameof(SwapSettings), new { storeId = store.Id });
    }

    static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static bool IsEvmAddress(string value) =>
        value.Length == 42 && value.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase) && value.Skip(2).All(System.Uri.IsHexDigit);

    static bool IsBase58(string value, int min, int max) =>
        value.Length >= min && value.Length <= max && value.All(c => "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz".Contains(c));

    static void FillChains(SwapSettingsViewModel vm)
    {
        var posted = vm.Chains.ToDictionary(c => c.Code, c => c.Enabled);
        vm.Chains = SwapSourceChain.All.Select(c => new SwapChainViewModel
        {
            Code = c.Code,
            Name = c.Name,
            PaymentMethodId = c.PaymentMethodId.ToString(),
            Image = c.Image,
            Provider = c.Provider,
            Enabled = posted.GetValueOrDefault(c.Code)
        }).ToList();
    }
}

public class SwapSettingsViewModel
{
    public bool HasBtcWallet { get; set; }

    [Display(Name = "Enable")]
    public bool Enabled { get; set; } = true;

    [Display(Name = "LI.FI integrator name")]
    public string? Integrator { get; set; }

    [Display(Name = "LI.FI API key (optional)")]
    public string? ApiKey { get; set; }

    [Display(Name = "Integrator fee (%)")]
    [Range(0, 9.99)]
    public decimal FeePercent { get; set; }

    [Display(Name = "Amount buffer (%)")]
    [Range(0, 5)]
    public decimal BufferPercent { get; set; } = SwapDefaults.BufferPercent;

    [Display(Name = "WalletConnect project ID (optional)")]
    public string? WalletConnectProjectId { get; set; }

    [Display(Name = "NEAR Intents API key")]
    public string? IntentsApiKey { get; set; }

    [Display(Name = "Refund address for EVM networks (Ethereum, Base, Polygon...)")]
    public string? RefundEvm { get; set; }

    [Display(Name = "Refund address on Solana")]
    public string? RefundSolana { get; set; }

    [Display(Name = "Refund address on Tron")]
    public string? RefundTron { get; set; }

    [Display(Name = "Stablecoin price tolerance (basis points)")]
    [Range(1, 1000)]
    public int IntentsSlippageBps { get; set; } = SwapDefaults.IntentsSlippageBps;

    /// <summary>Not on the page; kept so a save does not drop a custom endpoint (used for testing).</summary>
    public string? IntentsUrl { get; set; }

    public List<SwapChainViewModel> Chains { get; set; } = new();
}

/// <summary>
/// Only Code and Enabled are posted back. The rest is display-only, so it must not be validated:
/// MVC treats non-nullable strings as required, and the missing values made every save fail silently.
/// </summary>
public class SwapChainViewModel
{
    public string Code { get; set; } = "";
    [ValidateNever] public string Name { get; set; } = "";
    [ValidateNever] public string PaymentMethodId { get; set; } = "";
    [ValidateNever] public string Image { get; set; } = "";
    [ValidateNever] public SwapProvider Provider { get; set; }
    public bool Enabled { get; set; }
}

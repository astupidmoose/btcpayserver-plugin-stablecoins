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
public class UISwapStoreController(StoreRepository storeRepository, PaymentMethodHandlerDictionary handlers, SwapSecrets secrets,
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
            HasApiKey = !string.IsNullOrEmpty(any.ApiKey),
            FeePercent = any.Fee * 100m,
            BufferPercent = any.BufferPercent,
            WalletConnectProjectId = any.WalletConnectProjectId,
            HasIntentsApiKey = !string.IsNullOrEmpty(any.IntentsApiKey),
            RefundEvm = any.RefundEvm,
            RefundSolana = any.RefundSolana,
            RefundTron = any.RefundTron,
            IntentsSlippageBps = any.IntentsSlippageBps,
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
        var saved = await storeRepository.GetSettingAsync<SwapToBtcPaymentMethodConfig>(store.Id, StoreSettingName);
        vm.HasApiKey = !string.IsNullOrEmpty(saved?.ApiKey);
        vm.HasIntentsApiKey = !string.IsNullOrEmpty(saved?.IntentsApiKey);
        var config = new SwapToBtcPaymentMethodConfig
        {
            Integrator = vm.Integrator?.Trim(),
            // Keys are write-only on the page: an empty field keeps the saved key.
            ApiKey = vm.ClearApiKey ? null : Clean(vm.ApiKey) ?? saved?.ApiKey,
            Fee = vm.FeePercent / 100m,
            BufferPercent = vm.BufferPercent,
            WalletConnectProjectId = string.IsNullOrWhiteSpace(vm.WalletConnectProjectId) ? null : vm.WalletConnectProjectId.Trim(),
            IntentsApiKey = vm.ClearIntentsApiKey ? null : Clean(vm.IntentsApiKey) ?? saved?.IntentsApiKey,
            RefundEvm = Clean(vm.RefundEvm),
            RefundSolana = Clean(vm.RefundSolana),
            RefundTron = Clean(vm.RefundTron),
            IntentsSlippageBps = vm.IntentsSlippageBps
        };
        foreach (var (field, message) in SwapToBtcPaymentMethodHandler.Validate(config))
            ModelState.AddModelError(field == nameof(config.Fee) ? nameof(vm.FeePercent) : field, message);
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
        // The payment method configs are returned by BTCPay's API: they get everything but the keys.
        var withoutKeys = Newtonsoft.Json.JsonConvert.DeserializeObject<SwapToBtcPaymentMethodConfig>(Newtonsoft.Json.JsonConvert.SerializeObject(config))!;
        withoutKeys.ApiKey = null;
        withoutKeys.IntentsApiKey = null;
        foreach (var chain in SwapSourceChain.PaymentMethods)
        {
            var handler = handlers[chain.PaymentMethodId];
            var on = vm.Enabled && enabled.Contains(chain);
            // Disabled chains are removed, not just excluded, so store integrations stop listing them.
            store.SetPaymentMethodConfig(handler, on ? withoutKeys : null);
            blob.SetExcluded(chain.PaymentMethodId, !on);
        }
        store.SetStoreBlob(blob);
        await storeRepository.UpdateStore(store);
        await storeRepository.UpdateSetting(store.Id, StoreSettingName, config);
        secrets.Set(store.Id, config);
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

    public static bool IsEvmAddress(string value) => AddressChecks.IsEvmAddress(value);

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
    public bool HasApiKey { get; set; }
    [Display(Name = "Remove the saved key")]
    public bool ClearApiKey { get; set; }

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
    public bool HasIntentsApiKey { get; set; }
    [Display(Name = "Remove the saved key")]
    public bool ClearIntentsApiKey { get; set; }

    [Display(Name = "Refund address for EVM networks (Ethereum, Base, Polygon...)")]
    public string? RefundEvm { get; set; }

    [Display(Name = "Refund address on Solana")]
    public string? RefundSolana { get; set; }

    [Display(Name = "Refund address on Tron")]
    public string? RefundTron { get; set; }

    [Display(Name = "Stablecoin price tolerance (basis points)")]
    [Range(1, 1000)]
    public int IntentsSlippageBps { get; set; } = SwapDefaults.IntentsSlippageBps;


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

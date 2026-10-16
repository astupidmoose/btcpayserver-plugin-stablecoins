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

namespace BTCPayServer.Plugins.Stablecoins.Controllers;

[Route("stores/{storeId}/stablecoins/swap")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class UISwapStoreController(StoreRepository storeRepository, PaymentMethodHandlerDictionary handlers) : Controller
{
    public const string SettingsView = StablecoinsPlugin.ViewsDirectory + "/SwapSettings.cshtml";

    StoreData StoreData => HttpContext.GetStoreData();

    [HttpGet("")]
    public IActionResult SwapSettings()
    {
        var store = StoreData;
        var excluded = store.GetStoreBlob().GetExcludedPaymentMethods();
        var configs = SwapSourceChain.All
            .Select(c => (Chain: c, Config: store.GetPaymentMethodConfig<SwapToBtcPaymentMethodConfig>(c.PaymentMethodId, handlers)))
            .ToList();
        var any = configs.Select(c => c.Config).FirstOrDefault(c => c is not null) ?? new SwapToBtcPaymentMethodConfig();
        return View(SettingsView, new SwapSettingsViewModel
        {
            HasBtcWallet = store.GetDerivationSchemeSettings(handlers, "BTC") is not null,
            Integrator = any.Integrator ?? SwapDefaults.Integrator,
            ApiKey = any.ApiKey,
            FeePercent = any.Fee * 100m,
            BufferPercent = any.BufferPercent,
            WalletConnectProjectId = any.WalletConnectProjectId,
            ThornodeUrl = any.ThornodeUrl,
            ThorAffiliate = any.ThorAffiliate,
            ThorAffiliateBps = any.ThorAffiliateBps,
            ThorToleranceBps = any.ThorToleranceBps,
            Chains = configs.Select(c => new SwapChainViewModel
            {
                Code = c.Chain.Code,
                Name = c.Chain.Name,
                PaymentMethodId = c.Chain.PaymentMethodId.ToString(),
                Image = c.Chain.Image,
                Provider = c.Chain.Provider,
                Enabled = c.Config is not null && !excluded.Match(c.Chain.PaymentMethodId)
            }).ToList()
        });
    }

    [HttpPost("")]
    public async Task<IActionResult> SwapSettings(SwapSettingsViewModel vm)
    {
        var store = StoreData;
        vm.HasBtcWallet = store.GetDerivationSchemeSettings(handlers, "BTC") is not null;
        var config = new SwapToBtcPaymentMethodConfig
        {
            Integrator = vm.Integrator?.Trim(),
            ApiKey = string.IsNullOrWhiteSpace(vm.ApiKey) ? null : vm.ApiKey.Trim(),
            Fee = vm.FeePercent / 100m,
            BufferPercent = vm.BufferPercent,
            WalletConnectProjectId = string.IsNullOrWhiteSpace(vm.WalletConnectProjectId) ? null : vm.WalletConnectProjectId.Trim(),
            ThornodeUrl = string.IsNullOrWhiteSpace(vm.ThornodeUrl) ? null : vm.ThornodeUrl.Trim(),
            ThorAffiliate = string.IsNullOrWhiteSpace(vm.ThorAffiliate) ? null : vm.ThorAffiliate.Trim(),
            ThorAffiliateBps = vm.ThorAffiliateBps,
            ThorToleranceBps = vm.ThorToleranceBps
        };
        foreach (var (field, message) in SwapToBtcPaymentMethodHandler.Validate(config))
            ModelState.AddModelError(field == nameof(config.Fee) ? nameof(vm.FeePercent) : field, message);
        if (string.IsNullOrEmpty(config.ThorAffiliate) && config.ThorAffiliateBps > 0)
            ModelState.AddModelError(nameof(vm.ThorAffiliate), "Set the THORName that receives the affiliate fee, or set the fee to 0.");
        var enabled = vm.Chains.Where(c => c.Enabled).Select(c => SwapSourceChain.Find(c.Code)).OfType<SwapSourceChain>().ToHashSet();
        if (enabled.Count > 0 && !vm.HasBtcWallet)
            ModelState.AddModelError(string.Empty, "Set up the store's BTC on-chain wallet first: swapped BTC is paid into it.");
        if (!ModelState.IsValid)
        {
            FillChains(vm);
            return View(SettingsView, vm);
        }

        var blob = store.GetStoreBlob();
        foreach (var chain in SwapSourceChain.All)
        {
            var handler = handlers[chain.PaymentMethodId];
            var on = enabled.Contains(chain);
            // Disabled chains are removed, not just excluded, so WooCommerce stops listing them.
            store.SetPaymentMethodConfig(handler, on ? config : null);
            blob.SetExcluded(chain.PaymentMethodId, !on);
        }
        store.SetStoreBlob(blob);
        await storeRepository.UpdateStore(store);

        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = StatusMessageModel.StatusSeverity.Success,
            Message = enabled.Count == 0
                ? "Swap settings saved. No chains are enabled."
                : $"Swap settings saved. Customers can pay from {string.Join(", ", SwapSourceChain.All.Where(enabled.Contains).Select(c => c.Name))}."
        });
        return RedirectToAction(nameof(SwapSettings), new { storeId = store.Id });
    }

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

    [Display(Name = "THORNode URL (optional)")]
    public string? ThornodeUrl { get; set; }

    [Display(Name = "THORChain affiliate THORName (optional)")]
    public string? ThorAffiliate { get; set; }

    [Display(Name = "THORChain affiliate fee (basis points)")]
    [Range(0, 1000)]
    public int ThorAffiliateBps { get; set; }

    [Display(Name = "THORChain price tolerance (basis points)")]
    [Range(10, 5000)]
    public int ThorToleranceBps { get; set; } = SwapDefaults.ThorToleranceBps;

    public List<SwapChainViewModel> Chains { get; set; } = new();
}

public class SwapChainViewModel
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string PaymentMethodId { get; set; } = "";
    public string Image { get; set; } = "";
    public SwapProvider Provider { get; set; }
    public bool Enabled { get; set; }
}

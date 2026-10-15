#nullable enable
using System;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Stablecoins.Payments;
using BTCPayServer.Plugins.Stablecoins.Solana;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Stablecoins.Controllers;

[Route("stores/{storeId}/usdc/solana")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class UIUsdcStoreController(
    StoreRepository storeRepository,
    PaymentMethodHandlerDictionary handlers,
    SolanaChainConfiguration chain,
    SolanaRpcClient rpc) : Controller
{
    public const string SettingsView = StablecoinsPlugin.ViewsDirectory + "/SolanaSettings.cshtml";

    StoreData StoreData => HttpContext.GetStoreData();

    [HttpGet("")]
    public IActionResult SolanaSettings()
    {
        var store = StoreData;
        var config = store.GetPaymentMethodConfig<SolanaUsdcPaymentMethodConfig>(StablecoinsPlugin.SolanaUsdcPaymentMethodId, handlers);
        var excluded = store.GetStoreBlob().GetExcludedPaymentMethods().Match(StablecoinsPlugin.SolanaUsdcPaymentMethodId);
        return View(SettingsView, new SolanaSettingsViewModel
        {
            Address = config?.Address,
            Enabled = config is not null && !excluded,
            Cluster = chain.Cluster,
            UsdcMint = chain.UsdcMint
        });
    }

    [HttpPost("")]
    public async Task<IActionResult> SolanaSettings(SolanaSettingsViewModel vm, CancellationToken cancellationToken)
    {
        vm.Cluster = chain.Cluster;
        vm.UsdcMint = chain.UsdcMint;
        vm.Address = vm.Address?.Trim();
        if (!SolanaPay.IsPublicKey(vm.Address))
            ModelState.AddModelError(nameof(vm.Address), "Enter a Solana wallet address (base58, 32 bytes).");
        if (!ModelState.IsValid)
            return View(SettingsView, vm);

        var store = StoreData;
        var handler = (SolanaUsdcPaymentMethodHandler)handlers[StablecoinsPlugin.SolanaUsdcPaymentMethodId];
        store.SetPaymentMethodConfig(handler, new SolanaUsdcPaymentMethodConfig { Address = vm.Address });
        var blob = store.GetStoreBlob();
        blob.SetExcluded(StablecoinsPlugin.SolanaUsdcPaymentMethodId, !vm.Enabled);
        store.SetStoreBlob(blob);
        await storeRepository.UpdateStore(store);

        // Solana Pay transfers fail if the recipient has no USDC token account, so check for one.
        string? warning = null;
        try
        {
            var accounts = await rpc.GetTokenAccountsByOwner(vm.Address!, chain.UsdcMint, cancellationToken);
            if (accounts.Count == 0)
                warning = $"Saved, but this wallet has no USDC token account on {chain.Cluster} yet. " +
                          "Customers' payments will fail until it has one: receive any amount of USDC to it first.";
        }
        catch (Exception ex)
        {
            warning = $"Saved, but the Solana RPC could not be reached to check the wallet's USDC account ({ex.Message}).";
        }

        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = warning is null ? StatusMessageModel.StatusSeverity.Success : StatusMessageModel.StatusSeverity.Warning,
            Message = warning ?? "USDC (Solana) settings saved."
        });
        return RedirectToAction(nameof(SolanaSettings), new { storeId = store.Id });
    }
}

public class SolanaSettingsViewModel
{
    [Display(Name = "Solana wallet address")]
    public string? Address { get; set; }

    [Display(Name = "Enable USDC (Solana) at checkout")]
    public bool Enabled { get; set; }

    public string Cluster { get; set; } = "";
    public string UsdcMint { get; set; } = "";
}

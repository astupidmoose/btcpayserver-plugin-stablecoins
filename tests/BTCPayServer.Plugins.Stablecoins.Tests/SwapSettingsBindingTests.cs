using System.Globalization;
using BTCPayServer.Plugins.Stablecoins.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace BTCPayServer.Plugins.Stablecoins.Tests;

public class SwapSettingsBindingTests
{
    /// <summary>
    /// Binds a post shaped exactly like the settings form (each chain sends only Code and Enabled)
    /// through MVC's own binding and validation, which is what decides whether Save goes through.
    /// </summary>
    static async Task<(SwapSettingsViewModel Model, ModelStateDictionary State)> Post(Dictionary<string, StringValues> form)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddMvcCore().AddDataAnnotations();
        var provider = services.BuildServiceProvider();
        var context = new ActionContext(new DefaultHttpContext { RequestServices = provider }, new RouteData(), new ActionDescriptor());
        var metadataProvider = provider.GetRequiredService<IModelMetadataProvider>();
        var parameter = new ParameterDescriptor { Name = "vm", ParameterType = typeof(SwapSettingsViewModel) };
        var metadata = metadataProvider.GetMetadataForType(typeof(SwapSettingsViewModel));
        var binder = provider.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext
        {
            Metadata = metadata, BindingInfo = new BindingInfo(), CacheToken = parameter
        });
        var result = await provider.GetRequiredService<ParameterBinder>().BindModelAsync(context, binder,
            new FormValueProvider(BindingSource.Form, new FormCollection(form), CultureInfo.InvariantCulture), parameter, metadata, null, null);
        return ((SwapSettingsViewModel)result.Model!, context.ModelState);
    }

    [Fact]
    public async Task AcceptsWhatTheFormPosts()
    {
        var (vm, state) = await Post(new()
        {
            ["Integrator"] = "coincards",
            ["FeePercent"] = "0",
            ["BufferPercent"] = "1",
            ["ThorToleranceBps"] = "300",
            ["ThorAffiliateBps"] = "0",
            ["Chains[0].Code"] = "ETHEREUM",
            ["Chains[0].Enabled"] = new StringValues(["true", "false"]),
            ["Chains[1].Code"] = "BASE",
            ["Chains[1].Enabled"] = "false",
        });

        Assert.True(state.IsValid, string.Join("; ", state.Where(e => e.Value!.Errors.Count > 0)
            .Select(e => $"{e.Key}: {string.Join(",", e.Value!.Errors.Select(x => x.ErrorMessage))}")));
        Assert.Equal(1m, vm.BufferPercent);
        Assert.True(vm.Chains[0].Enabled);
        Assert.False(vm.Chains[1].Enabled);
    }
}

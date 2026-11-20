using Amifolio.Context;
using Microsoft.UI.Xaml;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private async Task<object> RunConfigurationRegressionAsync()
    {
        var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException("Configuration regression: " + name); checks.Add(name); }
        await NavigateAsync("help");
        ModelEndpoint.Text = "https://example.com/v1"; ModelName.Text = "smoke-model";
        ModelKey.Password = "dummy-native-config-key"; RemoteAllowed.IsOn = true;
        SaveModelConfiguration(this, new RoutedEventArgs());
        while (operation is not null) await Task.Delay(20);
        var saved = await new ModelConfigurationStore(dataRoot).LoadAsync();
        Check(saved.ApiKey == "dummy-native-config-key", "save-button-persists-key");
        Check(saved.Model == "smoke-model" && saved.RemoteAllowed, "save-button-persists-model-and-permission");
        var text = await File.ReadAllTextAsync(Path.Combine(dataRoot, "settings", "ai.json"));
        Check(!text.Contains("dummy-native-config-key"), "no-plaintext-key-on-disk");
        loadingModelConfiguration=true;
        try{ModelKey.Password = ""; ModelName.Text = ""; RemoteAllowed.IsOn = false;}
        finally{loadingModelConfiguration=false;}
        await LoadModelConfigurationAsync(CancellationToken.None);
        Check(ModelKey.Password == "dummy-native-config-key" && ModelName.Text == "smoke-model" && RemoteAllowed.IsOn, "startup-loader-refills-controls");
        ClearModelConfiguration(this, new RoutedEventArgs());
        while (operation is not null) await Task.Delay(20);
        Check(ModelKey.Password == "" && !RemoteAllowed.IsOn, "clear-button-resets-key-and-permission");
        var cleared = await new ModelConfigurationStore(dataRoot).LoadAsync();
        Check(cleared.ApiKey == "" && !cleared.RemoteAllowed, "clear-button-removes-saved-key");
        Check(!File.Exists(Path.Combine(dataRoot, "settings", "ai.json")), "saved-file-removed");
        // Leave a dummy configuration for the separately launched restart scenario.
        ModelEndpoint.Text="https://example.com/v1";ModelName.Text="smoke-model";ModelKey.Password="dummy-native-config-key";RemoteAllowed.IsOn=true;
        SaveModelConfiguration(this,new RoutedEventArgs());while(operation is not null)await Task.Delay(20);
        return new { checks };
    }

    private async Task<object> RunConfigurationReopenRegressionAsync()
    {
        var checks=new List<string>();
        void Check(bool value,string name){if(!value)throw new InvalidOperationException("Configuration restart regression: "+name);checks.Add(name);}
        Check(ModelKey.Password=="dummy-native-config-key","new-process-startup-restores-key");
        Check(ModelName.Text=="smoke-model"&&ModelEndpoint.Text=="https://example.com/v1","new-process-startup-restores-model-and-address");
        Check(RemoteAllowed.IsOn,"new-process-startup-restores-explicit-permission");
        ClearModelConfiguration(this,new RoutedEventArgs());while(operation is not null)await Task.Delay(20);
        Check(ModelKey.Password==""&&!RemoteAllowed.IsOn,"new-process-clear-resets-controls");
        var cleared=await new ModelConfigurationStore(dataRoot).LoadAsync();
        Check(cleared.ApiKey==""&&!cleared.RemoteAllowed&&!File.Exists(Path.Combine(dataRoot,"settings","ai.json")),"new-process-clear-removes-saved-configuration");
        return new{checks};
    }
}

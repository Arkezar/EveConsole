using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Windows.Input;
using EveConsole.Agent;
using EveConsole.Services;
using ReactiveUI;
using EveConsole.Localization;

namespace EveConsole.ViewModels;

/// <summary>A model in a role's list, or a fixed "None" / "Same as conversation" entry.</summary>
public abstract class ModelChoice : ReactiveObject
{
    public abstract string Id    { get; }
    public abstract string Label { get; }
}

public sealed class FixedModelChoice(string label) : ModelChoice
{
    public override string Id    => "";
    public override string Label => label;
}

/// <summary>A model's editable profile; provider-wide setup is shared through its owner.</summary>
public sealed class ModelProfileVm : ModelChoice, IDisposable
{
    private readonly AgentSettingsViewModel _owner;
    private readonly ModelProfile _original;
    private readonly IDisposable _relistSubscription;
    public AgentSettingsViewModel Owner => _owner;
    public override string Id => _original.Id;
    private AgentProviderDefinition? Definition => _owner.Providers.Find(_providerId);

    public ModelProfileVm(AgentSettingsViewModel owner, ModelProfile profile)
    {
        _owner = owner;
        _original = profile.Clone();
        _name = profile.Name;
        _providerId = profile.ProviderId;
        // Replacing items during selection makes the ComboBox write its old choice back.
        ServiceOptions = [.. owner.Providers.Definitions.Select(d => new ServiceOption(d.Id, d.Label))];
        if (Definition is null)
            ServiceOptions = [.. ServiceOptions, new(_providerId, string.Format(SettingsText.ProviderUnsupported, _providerId))];
        _model = profile.Model;
        _endpoint = profile.Endpoint;
        _think = profile.Think;
        TestModelCommand = ReactiveCommand.CreateFromTask(TestAsync);
        ModelList = new ServiceListChoice(() => _model, id => Model = id, ListSource,
            n => Plurals.Format(SettingsText.ResourceManager, nameof(SettingsText.ProviderModelsFoundOther), n, Definition?.Name ?? _providerId),
            () => Definition?.IsLocal == true ? SettingsText.ModelListEmptyLocal : SettingsText.ModelListEmpty,
            ServiceListChoice.Holds.Models, SettingsText.TypeModelNameInstead, Listed);
        _relistSubscription = _relist.Throttle(TimeSpan.FromMilliseconds(700))
               .ObserveOnUi("model list")
               .Subscribe(settled => { if (ReferenceEquals(_owner.SelectedModel, this)) _ = ModelList.LoadAsync(); });
    }

    public void Dispose()
    {
        _relistSubscription.Dispose();
        _relist.Dispose();
        ModelList.Dispose();
    }

    public ModelProfile ToProfile()
    {
        // Keep unsupported settings as well as the fields this build knows how to edit.
        var profile = _original.Clone();
        profile.Name = (_name ?? "").Trim();
        profile.ProviderId = _providerId;
        profile.Model = (_model ?? "").Trim();
        profile.Endpoint = (_endpoint ?? "").Trim();
        profile.Think = _think;
        return profile;
    }

    public override string Label => Definition is null ? NameWatermark
        : string.Format(Definition.IsLocal ? SettingsText.ModelLabelFree : SettingsText.ModelLabelPaid,
            !string.IsNullOrWhiteSpace(_name) ? _name.Trim() : NameWatermark);

    private void Edited()
    {
        this.RaisePropertyChanged(nameof(Label));
        this.RaisePropertyChanged(nameof(NameWatermark));
        _owner.OnModelsChanged();
    }

    private string _name;
    public string Name
    {
        get => _name;
        set { this.RaiseAndSetIfChanged(ref _name, value); Edited(); }
    }
    public string NameWatermark => _owner.Providers.Describe(ToProfile());

    public sealed record ServiceOption(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    public IReadOnlyList<ServiceOption> ServiceOptions { get; }

    public ServiceOption? SelectedService
    {
        get => ServiceOptions.FirstOrDefault(s => s.Id == _providerId);
        set { if (value is not null && value.Id != _providerId) ProviderId = value.Id; }
    }

    private string _providerId;
    public string ProviderId
    {
        get => _providerId;
        set
        {
            if (value == _providerId) return;
            this.RaiseAndSetIfChanged(ref _providerId, value);
            foreach (var property in new[] { nameof(SelectedService), nameof(ShowApiKey),
                nameof(ShowEndpoint), nameof(ShowThink), nameof(ShowClaude), nameof(ApiKeyLabel), nameof(ApiKeyUrl),
                nameof(EndpointWatermark), nameof(ModelWatermark), nameof(ModelHelp) })
                this.RaisePropertyChanged(property);
            RefreshProviderSettings();
            Edited();
            Model = "";
            _ = ModelList.LoadAsync();
        }
    }

    public bool ShowApiKey => Definition?.RequiresApiKey == true;
    public bool ShowEndpoint => Definition is not null;
    public bool ShowThink => Definition?.IsLocal == true;
    public bool ShowClaude => _providerId == AgentProviders.Claude;
    public string ApiKeyLabel => string.Format(SettingsText.ProviderKeyLabel, Definition?.Name ?? _providerId);
    public string ApiKeyUrl => Definition?.KeyUrl ?? "";
    public string EndpointWatermark => Definition?.IsLocal == true ? "http://gpu-box:11434" : Definition?.BaseUrl ?? "";

    public string ApiKey
    {
        get => _owner.ProviderSettings.ConfigurationFor(_providerId).ApiKey;
        set
        {
            if (value == ApiKey) return;
            _owner.ProviderSettings.ConfigurationFor(_providerId).ApiKey = value;
            _owner.ProviderEdited(_providerId);
        }
    }

    internal void RefreshProviderSettings()
    {
        this.RaisePropertyChanged(nameof(ApiKey));
        this.RaisePropertyChanged(nameof(Endpoint));
        KeyChanged();
    }

    private string _endpoint;
    public string Endpoint
    {
        get => Definition?.IsLocal == true ? _endpoint : _owner.ProviderSettings.ConfigurationFor(_providerId).Endpoint;
        set
        {
            if (value == Endpoint) return;
            if (Definition?.IsLocal == true)
            {
                this.RaiseAndSetIfChanged(ref _endpoint, value);
                KeyChanged();
            }
            else
            {
                _owner.ProviderSettings.ConfigurationFor(_providerId).Endpoint = value;
                _owner.ProviderEdited(_providerId);
            }
            Edited();
        }
    }

    private string _model;
    public string Model
    {
        get => _model;
        set
        {
            this.RaiseAndSetIfChanged(ref _model, value);
            ModelList.ValueChanged();
            Edited();
        }
    }
    public string ModelWatermark => Definition?.IsLocal == true ? SettingsText.ModelHintServerNames : SettingsText.ModelHintServiceNames;
    public string ModelHelp => Definition?.IsLocal == true ? SettingsText.ModelHelpLocal : "";
    public ServiceListChoice ModelList { get; }
    private readonly Subject<Unit> _relist = new();
    internal void KeyChanged() => _relist.OnNext(Unit.Default);

    private ServiceListChoice.Source ListSource()
    {
        var model = ToProfile();
        var settings = _owner.ProviderSettings.Clone();
        var configuration = settings.ConfigurationFor(_providerId);
        var question = $"{_providerId}|{HashCode.Combine(_owner.Providers.Endpoint(model, settings), configuration.ApiKey, configuration.CacheTtl)}";
        return _owner.Providers.Validate(model, settings, requireModel: false) is { } why
            ? ServiceListChoice.Source.Unavailable(why, question)
            : new(null, question, ct => _owner.Providers.ListModelsAsync(model, settings, ct));
    }

    private void Listed(IReadOnlyList<ModelListing> listed)
    {
        if (Definition is { IsLocal: false } definition) _owner.AddListedRates(("llm", definition.Name), listed);
    }

    private bool _think;
    public bool Think
    {
        get => _think;
        set => this.RaiseAndSetIfChanged(ref _think, value);
    }

    public ICommand TestModelCommand { get; }
    private string _testStatus = "";
    public string TestStatus
    {
        get => _testStatus;
        private set { this.RaiseAndSetIfChanged(ref _testStatus, value); this.RaisePropertyChanged(nameof(HasTestStatus)); }
    }
    public bool HasTestStatus => _testStatus.Length > 0;
    private bool _testOk;
    public bool TestOk { get => _testOk; private set => this.RaiseAndSetIfChanged(ref _testOk, value); }
    private bool _testFailed;
    public bool TestFailed { get => _testFailed; private set => this.RaiseAndSetIfChanged(ref _testFailed, value); }

    private async Task TestAsync()
    {
        TestOk = false;
        TestFailed = false;
        TestStatus = SettingsText.Testing;
        (bool Ok, string Message) result;
        try { result = await _owner.TestModelAsync(this); }
        catch (Exception ex) { result = (false, ex.GetBaseException().Message); }
        TestOk = result.Ok;
        TestFailed = !result.Ok;
        TestStatus = result.Message;
    }
}

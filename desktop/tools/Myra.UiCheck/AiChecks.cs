using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Myra.App.Services;
using Myra.App.ViewModels;
using Myra.App.Views;
using Myra.Core;

/// Myra AI checks: entry points, the disabled state, the consent prompt, a grounded result card,
/// reviewed action preview / apply / undo, keyboard, and AI Settings key storage.
/// Every model call goes to a fake HttpMessageHandler behind the real OpenAI provider. No network, no paid calls.
/// Call site in Program.cs, after ShellChecks: await AiChecks.RunAsync(window, vm, services, output, Check, Pump);
internal static class AiChecks
{
    /// Answers like the OpenAI Responses API. The intent step gets "{}"; the answer step gets Answer.
    private sealed class FakeOpenAi : HttpMessageHandler
    {
        private readonly List<string> _requests = [];
        public string Answer { get; set; } = "{}";

        public int Count
        {
            get { lock (_requests) return _requests.Count; }
        }

        public IReadOnlyList<string> Requests
        {
            get { lock (_requests) return _requests.ToList(); }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_requests) _requests.Add(request.RequestUri + "\n" + body);
            if (request.Method == HttpMethod.Get) return Json("{\"data\":[{\"id\":\"gpt-fixture\"}]}");
            var instructions = JsonNode.Parse(body)?["instructions"]?.GetValue<string>() ?? "";
            var text = instructions.Contains("Extract catalogue", StringComparison.Ordinal) ? "{}" : Answer;
            var payload = new JsonObject
            {
                ["output"] = new JsonArray(new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text }),
                }),
            };
            return Json(payload.ToJsonString());
        }

        private static HttpResponseMessage Json(string text) =>
            new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }

    public static async Task RunAsync(MainWindow window, MainViewModel vm, AppServices services, string output,
        Action<bool, string> check, Func<Func<bool>, int, Task> pump)
    {
        void Save(Window target, string name)
        {
            for (var i = 0; i < 3; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
            target.CaptureRenderedFrame()?.Save(Path.Combine(output, name));
            Console.WriteLine("saved " + name);
        }

        T? Owned<T>(Window owner) where T : Window => owner.OwnedWindows.OfType<T>().FirstOrDefault(w => w.IsVisible);
        static T? Named<T>(Window owner, string name) where T : Control =>
            owner.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        vm.Section = ShellSection.Home;
        await pump(() => !vm.Home.IsPreparing, 5000);
        var catalogue = services.Entertainment.Catalogue;

        // ---------- Nothing runs at launch; the default provider is Disabled ----------
        check(services.Ai.CurrentProvider is null && services.Ai.Settings.Provider == AiProviderIds.Disabled,
            "Myra AI starts with the Disabled provider");
        check(services.Ai.RequestsToday == 0 && services.Ai.Response is null && !services.Ai.IsBusy,
            "no AI request ran at launch or during indexing");

        // ---------- Home entry point opens the workspace on Smart Pick (disabled state) ----------
        var homeTask = vm.Home.OpenAiCommand.ExecuteAsync(AiFeature.SmartPick);
        await pump(() => Owned<AiWorkspaceWindow>(window) is not null, 5000);
        var workspaceWindow = Owned<AiWorkspaceWindow>(window);
        var home = workspaceWindow?.DataContext as AiWorkspaceViewModel;
        check(home is { Feature: AiFeature.SmartPick, IsProviderDisabled: true }, "Home › Myra AI › Help Me Choose opens the workspace on Smart Pick");
        if (workspaceWindow is not null && home is not null)
        {
            await pump(() => false, 200);
            check(workspaceWindow.FindControl<Border>("DisabledBanner")?.IsVisible == true && !home.RunCommand.CanExecute(null),
                "disabled provider: the workspace explains it and Ask Myra is disabled");
            Save(workspaceWindow, "30-ai-disabled.png");
            workspaceWindow.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            await pump(() => !workspaceWindow.IsVisible, 2000);
            check(!workspaceWindow.IsVisible, "Esc closes the workspace when nothing is running");
        }
        await pump(() => homeTask.IsCompleted, 3000);

        // ---------- Details entry point: AI tools close Details and open the workspace with the title ----------
        var series = catalogue.FirstOrDefault(t => t.Kind == EntertainmentKind.Series);
        if (series is not null)
        {
            var detailsTask = vm.ShowDetailsAsync(series);
            await pump(() => Owned<DetailWindow>(window) is not null, 5000);
            var details = Owned<DetailWindow>(window);
            var model = details?.DataContext as DetailViewModel;
            check(model is { CanAskAi: true } && details is not null && Named<Grid>(details, "AiRow")?.IsVisible == true, "Details shows Explore with Myra");
            model?.AskAiCommand.Execute(AiFeature.Recap);
            await pump(() => Owned<AiWorkspaceWindow>(window) is not null, 5000);
            var recapWindow = Owned<AiWorkspaceWindow>(window);
            var recap = recapWindow?.DataContext as AiWorkspaceViewModel;
            check(details?.IsVisible == false && recap is { Feature: AiFeature.Recap } && recap.SelectedIds.Contains(series.Id),
                "Details › AI tools › Episode Recap opens the workspace with the series selected");
            check(recap is { IsRecap: true, HasNoEpisodes: true, CanImportSubtitles: false },
                "a series without completed episodes offers no recap episode and no subtitle import");
            recap?.CloseCommand.Execute(null);
            await pump(() => detailsTask.IsCompleted, 3000);
        }

        // ---------- Fake cloud provider ----------
        var temp = Path.Combine(Path.GetTempPath(), "myra-aicheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var http = new FakeOpenAi();
        var secrets = new InMemorySecretStore();
        using var ai = new AiService(secrets, [new OpenAiProvider(new HttpClient(http)), new ClaudeProvider(new HttpClient(http))],
            Path.Combine(temp, "MyraAI.json"), Path.Combine(temp, "MyraAIUsage.json"));
        ai.UpdateSettings(s => s.WithProvider(AiProviderIds.OpenAI) with { Model = "gpt-fixture" });
        ai.SaveKey("sk-fixture-key");
        check(!ai.Settings.CloudConsent && ai.HasKey(), "fake OpenAI provider ready without cloud consent");

        using (var workspace = new AiWorkspaceViewModel(vm, AiFeature.SmartPick, ai: ai))
        {
            var aiWindow = new AiWorkspaceWindow(workspace) { Width = 1040, Height = 740 };
            var closed = aiWindow.ShowDialog<EntertainmentTitle?>(window);
            await pump(() => aiWindow.IsVisible, 2000);
            check(aiWindow.FindControl<TextBlock>("CloudNote")?.IsEffectivelyVisible == true && workspace.IsCloud, "cloud requests show the charges note");

            // Consent before the first cloud call. Enter in the request box asks Myra.
            http.Answer = "{\"summary\":\"A calm, thoughtful choice from your library.\",\"recommendations\":[{\"titleID\":\"t1\",\"reason\":\"It matches a quiet evening and is already indexed.\"}],\"actions\":[],\"tags\":[\"calm\"]}";
            workspace.Prompt = "something thoughtful for tonight";
            aiWindow.FindControl<TextBox>("PromptBox")?.Focus();
            aiWindow.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await pump(() => Owned<AiConsentDialog>(aiWindow) is not null, 5000);
            var consent = Owned<AiConsentDialog>(aiWindow);
            check(consent is { Kind: AiPermission.Cloud } && http.Count == 0,
                "Enter asks Myra, and the consent prompt appears before any HTTP request");
            if (consent is not null)
            {
                Save(consent, "31-ai-consent.png");
                Click(Named<Button>(consent, "AllowButton")!);
            }
            await pump(() => workspace.HasResult || workspace.HasError, 10000);
            check(ai.Settings.CloudConsent, "Allow saves the cloud permission");
            check(http.Count == 2 && http.Requests.All(r => r.StartsWith("https://api.openai.com/v1/responses")),
                $"Smart Pick made two generations to the fixed endpoint ({http.Count}): {workspace.ErrorMessage}");
            check(http.Requests.All(r => !r.Contains("127.0.0.1") && !r.Contains("http://")),
                "requests contain no source URL or address");
            var card = workspace.Cards.FirstOrDefault();
            check(card is not null && catalogue.Any(t => t.Id == card.Title.Id) && card.Reason.Contains("quiet evening"),
                "a grounded result card shows a real catalogue title with the reason");
            check(workspace.UsageText.Contains("Requests today: 2") && workspace.UsageText.Contains("estimate"),
                "usage shows requests today and labels tokens as estimates: " + workspace.UsageText);
            await pump(() => false, 200);
            Save(aiWindow, "32-ai-result.png");

            // History permission: Viewing Insights asks first; Not Now sends nothing.
            workspace.SelectedFeature = workspace.Features.First(f => f.Feature == AiFeature.Insights);
            var before = http.Count;
            var run = workspace.RunCommand.ExecuteAsync(null);
            await pump(() => Owned<AiConsentDialog>(aiWindow) is not null, 5000);
            var history = Owned<AiConsentDialog>(aiWindow);
            check(history is { Kind: AiPermission.History }, "Viewing Insights asks for the history permission");
            if (history is not null) Click(Named<Button>(history, "NotNowButton")!);
            await pump(() => run.IsCompleted, 3000);
            check(http.Count == before && !ai.Settings.ShareHistory && workspace.LocalMessage?.Contains("Nothing was sent") == true,
                "Not Now sends nothing and keeps history private");

            // A watchlist request asks for history before the paid intent generation; Not Now sends nothing.
            workspace.SelectedFeature = workspace.Features.First(f => f.Feature == AiFeature.NaturalSearch);
            workspace.Prompt = "comedies I saved to watch";
            var requestsBefore = ai.RequestsToday;
            run = workspace.RunCommand.ExecuteAsync(null);
            await pump(() => Owned<AiConsentDialog>(aiWindow) is not null, 5000);
            history = Owned<AiConsentDialog>(aiWindow);
            check(history is { Kind: AiPermission.History }, "a \"saved to watch\" search asks for the history permission first");
            if (history is not null) Click(Named<Button>(history, "NotNowButton")!);
            await pump(() => run.IsCompleted, 3000);
            check(http.Count == before && ai.RequestsToday == requestsBefore && !ai.Settings.ShareHistory
                  && workspace.LocalMessage?.Contains("Nothing was sent") == true,
                "declining history for a watchlist search sends nothing and uses no generation");

            // Reviewed action: preview, Apply, Undo.
            foreach (var id in services.Entertainment.Personal.Watchlist.ToList()) services.Entertainment.ToggleWatchlist(id);
            workspace.SelectedFeature = workspace.Features.First(f => f.Feature == AiFeature.Action);
            http.Answer = "{\"summary\":\"I can add this title for later.\",\"recommendations\":[],\"actions\":[{\"kind\":\"addWatchlist\",\"titleIDs\":[\"t1\"],\"value\":\"Later\"}],\"tags\":[]}";
            workspace.Prompt = "add a good film for later";
            await workspace.RunCommand.ExecuteAsync(null);
            await pump(() => workspace.HasActions || workspace.HasError, 10000);
            var action = workspace.Actions.FirstOrDefault();
            var target = action?.Action.TitleIds.FirstOrDefault();
            check(action is { ChangesPersonalData: true, ApplyLabel: "Apply" } && action.Preview.StartsWith("Adds 1 title")
                  && target is not null && !services.Entertainment.Personal.Watchlist.Contains(target),
                "an action is previewed and nothing changes before Apply: " + (action?.Preview ?? workspace.ErrorMessage));
            await pump(() => false, 200);
            Save(aiWindow, "33-ai-action-preview.png");
            if (action is not null && target is not null)
            {
                await action.ApplyCommand.ExecuteAsync(null);
                await pump(() => services.Entertainment.Personal.Watchlist.Contains(target), 3000);
                check(services.Entertainment.Personal.Watchlist.Contains(target) && action.IsApplied && workspace.CanUndo,
                    "Apply adds the title to the Watchlist and offers Undo");
                await pump(() => false, 200);
                Save(aiWindow, "34-ai-applied.png");
                await workspace.UndoCommand.ExecuteAsync(null);
                await pump(() => !services.Entertainment.Personal.Watchlist.Contains(target), 3000);
                check(!services.Entertainment.Personal.Watchlist.Contains(target) && !workspace.CanUndo && !action.IsApplied,
                    "Undo removes the title from the Watchlist again");
            }

            // Errors carry a recovery message.
            ai.UpdateSettings(s => s with { DailyRequestLimit = 1 });
            workspace.SelectedFeature = workspace.Features.First(f => f.Feature == AiFeature.Assistant);
            workspace.Prompt = "what should I start next";
            await workspace.RunCommand.ExecuteAsync(null);
            check(workspace.HasError && workspace.RecoveryText.Contains("daily limit") && workspace.ShowSettingsRecovery && workspace.IsLimitReached,
                "the daily limit stops the request and explains how to recover: " + workspace.ErrorMessage);
            await pump(() => false, 200);
            Save(aiWindow, "36-ai-limit.png");

            aiWindow.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            await pump(() => closed.IsCompleted, 2000);
            check(closed.IsCompleted, "Esc closes the workspace");
        }

        // ---------- AI Settings: the key goes to the secret store, never to settings files ----------
        var view = new AiSettingsView(services);
        var host = new Window { Content = new ScrollViewer { Content = view }, Width = 680, Height = 900 };
        host.Show();
        await pump(() => false, 300);
        var provider = Named<ComboBox>(host, "AiProviderBox");
        provider!.SelectedIndex = services.Ai.Providers.ToList().FindIndex(p => p.Id == AiProviderIds.OpenAI) + 1;
        await pump(() => Named<TextBox>(host, "AiKeyBox")?.IsEffectivelyVisible == true, 2000);
        const string key = "sk-uicheck-secret-value";
        Named<TextBox>(host, "AiKeyBox")!.Text = key;
        Click(Named<Button>(host, "AiSaveKey")!);
        await pump(() => false, 300);
        string Read(string path) => File.Exists(path) ? File.ReadAllText(path) : "";
        services.Store.Save();
        check(services.Ai.Settings.Provider == AiProviderIds.OpenAI && services.Secrets.Read(SecretNames.OpenAiApiKey) == key,
            "AI Settings saves the key in the secret store: " + view.LastMessage);
        check(!Read(AppPaths.StorePath).Contains(key) && !Read(AppPaths.AiSettingsPath).Contains(key) && Read(AppPaths.AiSettingsPath).Contains("openAI"),
            "the key is not in Myra.json or MyraAI.json");
        check(Named<TextBox>(host, "AiKeyBox")!.Text?.Length is null or 0, "the key box is cleared after saving");
        Named<NumericUpDown>(host, "AiDailyLimit")!.Value = 30;
        check(services.Ai.Settings.DailyRequestLimit == 30, "daily limit setting is saved");
        await pump(() => false, 200);
        host.CaptureRenderedFrame()?.Save(Path.Combine(output, "35-ai-settings.png"));
        Console.WriteLine("saved 35-ai-settings.png");
        services.Ai.RemoveKey();
        services.Ai.UpdateSettings(s => s.WithProvider(AiProviderIds.Disabled) with { DailyRequestLimit = 20 });
        check(!services.Ai.HasKey(AiProviderIds.OpenAI), "Remove Key deletes it from the secret store");
        host.Close();

        try
        {
            Directory.Delete(temp, true);
        }
        catch (IOException)
        {
        }
    }
}

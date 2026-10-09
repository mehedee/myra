using System.Globalization;
using Avalonia.Controls;
using Myra.App.ViewModels;

namespace Myra.App.Views;

public enum PlayerMenuKind
{
    Audio,
    Subtitles,
    Speed,
    Video,
    Chapters,
}

/// Builds the Audio, Subtitles, Speed, Video and Chapters menus. Each menu is built once per opening from a
/// snapshot of the player's lists, so tracks that appear while it is open never change it under the pointer.
public static class PlayerMenus
{
    private static readonly string[] Ratios = ["16:9", "4:3", "16:10", "1:1", "2.35:1"];
    private static readonly float[] Rates = [0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f, 3f, 4f];

    public static ContextMenu Build(PlayerViewModel player, PlayerMenuKind kind, Action loadSubtitleFile)
    {
        var menu = new ContextMenu { Placement = PlacementMode.Top };
        switch (kind)
        {
            case PlayerMenuKind.Audio:
                foreach (var track in player.AudioTracks.ToList())
                    Add(menu, track.Name, () => player.ChooseAudio(track.Id), checkedItem: track.Id == player.SelectedAudio?.Id, group: "audio");
                menu.Items.Add(new Separator());
                AddDelays(menu, audio: true, player);
                break;
            case PlayerMenuKind.Subtitles:
                Add(menu, "Off", () => player.ChooseSubtitle(-1), checkedItem: player.SelectedSubtitle?.Id is null or -1, group: "subtitle");
                foreach (var track in player.SubtitleTracks.Where(t => t.Id >= 0).ToList())
                    Add(menu, track.Name, () => player.ChooseSubtitle(track.Id), checkedItem: track.Id == player.SelectedSubtitle?.Id, group: "subtitle");
                menu.Items.Add(new Separator());
                Add(menu, "Find Online Subtitles…", player.FindOnlineSubtitles);
                Add(menu, "Load Subtitle File…", loadSubtitleFile);
                AddDelays(menu, audio: false, player);
                break;
            case PlayerMenuKind.Speed:
                foreach (var rate in Rates)
                    Add(menu, rate.ToString("0.##", CultureInfo.InvariantCulture) + "×", () => player.SetSpeed(rate),
                        checkedItem: Math.Abs(rate - player.Speed) < 0.001f, group: "speed");
                break;
            case PlayerMenuKind.Video:
                var aspect = Submenu(menu, "Aspect Ratio");
                Add(aspect, "Default", () => player.SetAspect(null));
                foreach (var ratio in Ratios) Add(aspect, ratio, () => player.SetAspect(ratio));
                var crop = Submenu(menu, "Crop");
                Add(crop, "Default", () => player.SetCrop(null));
                foreach (var ratio in Ratios) Add(crop, ratio, () => player.SetCrop(ratio));
                var deinterlace = Submenu(menu, "Deinterlace");
                Add(deinterlace, "Automatic", () => player.SetDeinterlace(PlayerDeinterlace.Automatic));
                Add(deinterlace, "On", () => player.SetDeinterlace(PlayerDeinterlace.On));
                Add(deinterlace, "Off", () => player.SetDeinterlace(PlayerDeinterlace.Off));
                break;
            default:
                foreach (var chapter in player.Chapters.ToList())
                    Add(menu, chapter.Name, () => player.ChooseChapter(chapter.Id), checkedItem: chapter.Id == player.SelectedChapter, group: "chapter");
                break;
        }
        return menu;
    }

    private static void AddDelays(ContextMenu menu, bool audio, PlayerViewModel player)
    {
        var sub = Submenu(menu, audio ? "Audio Synchronization" : "Subtitle Synchronization");
        Add(sub, "Earlier by 0.1s", () => { if (audio) player.ShiftAudioDelay(-0.1); else player.ShiftSubtitleDelay(-0.1); });
        Add(sub, "Later by 0.1s", () => { if (audio) player.ShiftAudioDelay(0.1); else player.ShiftSubtitleDelay(0.1); });
        var delay = audio ? player.AudioDelay : player.SubtitleDelay;
        Add(sub, $"Reset ({delay.ToString("0.0", CultureInfo.InvariantCulture)}s)", () => { if (audio) player.ResetAudioDelay(); else player.ResetSubtitleDelay(); });
    }

    private static MenuItem Submenu(ContextMenu menu, string title)
    {
        var item = new MenuItem { Header = title };
        menu.Items.Add(item);
        return item;
    }

    private static void Add(ContextMenu menu, string title, Action action, bool checkedItem = false, string? group = null) =>
        menu.Items.Add(Item(title, action, checkedItem, group));

    private static void Add(MenuItem parent, string title, Action action) =>
        parent.Items.Add(Item(title, action, false, null));

    private static MenuItem Item(string title, Action action, bool isChecked, string? group)
    {
        var item = new MenuItem { Header = title };
        if (group is not null)
        {
            item.ToggleType = MenuItemToggleType.Radio;
            item.GroupName = group;
            item.IsChecked = isChecked;
        }
        item.Click += (_, _) => action();
        return item;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Koda.Core;
using Koda.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Koda.Controls;

/// <summary>
/// An album's or playlist's download, as one button: Download, then how much is here while it
/// comes, then Downloaded (which, clicked, offers to remove it). The state is read from
/// <see cref="App.Downloads"/> and redrawn on its changes, never kept here.
/// </summary>
public sealed class DownloadButton : Button
{
    private readonly FontIcon _icon = new() { Glyph = "", FontSize = 14 };
    private readonly TextBlock _label = new() { Text = "Download" };
    private IReadOnlyList<Song> _songs = [];
    private bool _attached;
    private bool _busy;
    private int _renderQueued;

    public DownloadButton()
    {
        Padding = new Thickness(14, 7, 14, 7);
        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _icon, _label },
        };
        Click += OnClick;
        Loaded += (_, _) =>
        {
            if (!_attached)
            {
                _attached = true;
                App.Downloads.Changed += OnDownloadsChanged;
            }

            Render();
        };
        Unloaded += (_, _) =>
        {
            _attached = false;
            App.Downloads.Changed -= OnDownloadsChanged;
        };
        Render();
    }

    /// <summary>The songs the button speaks for; what it shows is computed from these.</summary>
    public IReadOnlyList<Song> Songs
    {
        get => _songs;
        set
        {
            _songs = value;
            Render();
        }
    }

    /// <summary>Every song of the collection, for one that loads in pages: fetched when Download is
    /// pressed. Null downloads <see cref="Songs"/>.</summary>
    public Func<Task<IReadOnlyList<Song>>>? AllSongs { get; set; }

    /// <summary>What the collection is called, for the confirmation and the note.</summary>
    public string Title { get; set; } = "";

    private void OnDownloadsChanged() => Refresh();

    public void Refresh()
    {
        if (Interlocked.Exchange(ref _renderQueued, 1) != 0) return;
        if (DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            Interlocked.Exchange(ref _renderQueued, 0);
            if (_attached) Render();
        }) != true) Interlocked.Exchange(ref _renderQueued, 0);
    }

    private void Render()
    {
        if (_busy) return;
        var state = App.Downloads.StateOf(_songs);
        IsEnabled = _songs.Count > 0;
        string text;
        string glyph;
        string help;
        switch (state.Phase)
        {
            case Downloads.Phase.Downloaded:
                (text, glyph, help) = ("Downloaded", "", "Downloaded: plays without a connection. Click to remove.");
                break;
            case Downloads.Phase.Downloading:
            case Downloads.Phase.Waiting:
                var percent = (int)Math.Round(state.Progress * 100);
                (text, glyph, help) = ($"Downloading {percent}%", "", "Downloading. Click to stop and remove.");
                break;
            default:
                (text, glyph, help) = ("Download", "", "Download to play without a connection");
                break;
        }

        _label.Text = text;
        _icon.Glyph = glyph;
        ToolTipService.SetToolTip(this, help);
        AutomationProperties.SetName(this, text);
    }

    private async void OnClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var songs = _songs.ToArray();
        var remove = App.Downloads.StateOf(songs).Phase != Downloads.Phase.None;
        _busy = true;
        IsEnabled = false;
        try
        {
            if (remove)
            {
                var name = string.IsNullOrEmpty(Title) ? "these songs" : Title;
                if (await Helpers.Dialogs.ConfirmAsync($"Remove the download of {name}?",
                        "They'll play from YouTube again, which needs a connection.", "Remove Download"))
                {
                    App.Downloads.Remove(songs);
                }

                return;
            }

            IReadOnlyList<Song> toDownload = songs;
            if (AllSongs is { } all)
            {
                _label.Text = "Getting songs…";
                toDownload = await all();
            }

            if (toDownload.Count > 0)
            {
                App.Downloads.Download(toDownload);
                App.Shell?.Note(toDownload.Count == 1
                    ? $"Downloading “{toDownload[0].Title}”."
                    : $"Downloading {toDownload.Count} songs.");
            }
        }
        catch (Exception error)
        {
            if (_attached) App.Shell?.Note($"Couldn't download this collection. {Helpers.Errors.Describe(error)}");
        }
        finally
        {
            _busy = false;
            if (_attached) Render();
        }
    }
}

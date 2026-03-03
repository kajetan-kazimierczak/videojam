using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using VideoJam.Engine;
using VideoJam.Model;
using VideoJam.Services;

namespace VideoJam.UI;

/// <summary>
/// Phase 2 integration harness — a minimal WPF window for manual A/V sync verification.
/// This code-behind is temporary and will be replaced by the full MVVM operator UI in Phase 5.
/// </summary>
public partial class MainWindow : Window {
	// ── Engines ───────────────────────────────────────────────────────────────

	private AudioEngine? audioEngine;
	private VideoEngine? videoEngine;
	private SyncCoordinator? syncCoordinator;

	// ── Loaded data ───────────────────────────────────────────────────────────

	private SongManifest? manifest;
	private Dictionary<int, VlcDisplayWindow> displayWindows = [];
	private string? videoFilePath;

	// ── Construction ──────────────────────────────────────────────────────────

	/// <inheritdoc />
	public MainWindow() {
		InitializeComponent();
	}

	// ── Button handlers ───────────────────────────────────────────────────────

	private void OnLoadClicked(object sender, RoutedEventArgs e) {
		var dialog = new OpenFolderDialog {
			Title = "Select a song folder containing audio stems",
		};

		if (dialog.ShowDialog(this) != true)
			return;

		CleanupAll();

		var folder = new DirectoryInfo(dialog.FolderName);

		try {
			// Phase 3 harness: auto-route each unique suffix to a sequential display index.
			// First distinct suffix found → display 0, second → display 1, etc.
			// Real routing comes from the .show file in Phase 4.
			var rawManifest = SongScanner.Scan(folder);
			var autoRouting = new Dictionary<string, int>();
			var nextDisplayIndex = DisplayManager.PRIMARY_DISPLAY_INDEX;
			foreach (var suffix in rawManifest.VideoFiles.Select(v => v.Suffix).Distinct())
				autoRouting[suffix] = nextDisplayIndex++;

			manifest = SongScanner.Scan(folder, displayRouting: autoRouting);

			FolderLabel.Text = folder.FullName;
			SetStatus("Folder loaded");

			UpdatePlayButtonState();
		}
		catch (Exception ex) {
			MessageBox.Show(
				$"Failed to load folder:\n\n{ex.Message}",
				"Load Error",
				MessageBoxButton.OK,
				MessageBoxImage.Error);
			SetStatus("Idle");
		}
	}

	private void OnLoadVideoClicked(object sender, RoutedEventArgs e) {
		var dialog = new OpenFileDialog {
			Title = "Select a video file (MP4)",
			Filter = "MP4 Video|*.mp4|All files|*.*",
		};

		if (dialog.ShowDialog(this) != true)
			return;

		videoFilePath = dialog.FileName;
		VideoLabel.Text = System.IO.Path.GetFileName(videoFilePath);

		UpdatePlayButtonState();
	}

	private async void OnPlayClicked(object sender, RoutedEventArgs e) {
		if (manifest is null) return;

		// FIX #1: Dispose stale engines from a previous natural-end cycle before
		// creating new ones. OnPlaybackEnded intentionally skips CleanupAll() to
		// preserve _manifest and _videoFilePath for replay, so stale engine instances
		// can survive until the next Play press. Dispose them here.
		if (audioEngine is not null) {
			audioEngine.PlaybackEnded -= OnPlaybackEnded;
			audioEngine.Dispose();
			audioEngine = null;
		}
		videoEngine?.Dispose();
		videoEngine = null;
		CloseDisplayWindow();

		SetStatus("Loading…");
		LoadButton.IsEnabled = false;
		LoadVideoButton.IsEnabled = false;
		PlayButton.IsEnabled = false;

		try {
			using var loggerFactory = LoggerFactory.Create(b =>
				b.AddConsole().SetMinimumLevel(LogLevel.Debug));

			// ── Build the audio pipeline ──────────────────────────────────────
			var channelSettings = new Dictionary<string, ChannelSettings>();
			foreach (var ch in manifest.AudioChannels) {
				channelSettings[ch.ChannelId] = new ChannelSettings {
					Level = 1.0f,
					Muted = ch.Type == AudioChannelType.VideoAudio,
				};
			}

			audioEngine = new AudioEngine(loggerFactory.CreateLogger<AudioEngine>());
			audioEngine.PlaybackEnded += OnPlaybackEnded;
			audioEngine.Load(manifest, channelSettings);

			// ── Build the video pipeline (if a video file was selected) ───────
			videoEngine = new VideoEngine(loggerFactory.CreateLogger<VideoEngine>());

			var manifestForVideo = manifest;

			// If the user selected a video file separately (outside of a song folder),
			// inject it into the manifest as a primary-display video entry.
			if (videoFilePath is not null
				&& !manifest.VideoFiles.Any(v => v.DisplayIndex == DisplayManager.PRIMARY_DISPLAY_INDEX)) {
				var injectedVideo = new VideoFileManifest(
					File: new FileInfo(videoFilePath),
					DisplayIndex: DisplayManager.PRIMARY_DISPLAY_INDEX,
					Suffix: string.Empty);

				manifestForVideo = manifest with {
					VideoFiles = manifest.VideoFiles.Prepend(injectedVideo).ToList(),
				};
			}

			if (manifestForVideo.VideoFiles.Count > 0) {
				// Create one VlcDisplayWindow per distinct display index referenced by the manifest.
				foreach (var idx in DisplayManager.GetRequiredDisplayIndices(manifestForVideo))
					displayWindows[idx] = DisplayManager.CreateWindowForDisplay(idx);

				await videoEngine.LoadAll(manifestForVideo, displayWindows);
			}

			// ── Synchronised start ────────────────────────────────────────────
			syncCoordinator = new SyncCoordinator(loggerFactory.CreateLogger<SyncCoordinator>());
			syncCoordinator.Start(audioEngine, videoEngine);

			SetStatus("Playing");
			StopButton.IsEnabled = true;
		}
		catch (Exception ex) {
			MessageBox.Show(
				$"Playback failed:\n\n{ex.Message}",
				"Playback Error",
				MessageBoxButton.OK,
				MessageBoxImage.Error);

			CleanupAll();
			SetStatus("Idle");
			LoadButton.IsEnabled = true;
			LoadVideoButton.IsEnabled = true;
			UpdatePlayButtonState();
		}
	}

	private void OnStopClicked(object sender, RoutedEventArgs e) {
		CleanupAll();
		SetStatus("Stopped");
		LoadButton.IsEnabled = true;
		LoadVideoButton.IsEnabled = true;
		StopButton.IsEnabled = false;
		UpdatePlayButtonState();
	}

	// ── PlaybackEnded callback (marshalled to UI thread by AudioEngine) ────────

	private void OnPlaybackEnded(object? sender, EventArgs e) {
		// Intentionally does NOT call CleanupAll() — _manifest and _videoFilePath
		// must survive so the user can press Play again for the same song.
		// Stale _audioEngine and _videoEngine are disposed at the top of OnPlayClicked.
		videoEngine?.Stop();
		CloseDisplayWindow();
		SetStatus("Stopped");
		LoadButton.IsEnabled = true;
		LoadVideoButton.IsEnabled = true;
		PlayButton.IsEnabled = manifest is not null;
		StopButton.IsEnabled = false;
	}

	// ── Helpers ───────────────────────────────────────────────────────────────

	private void SetStatus(string status) =>
		StatusLabel.Text = $"Status: {status}";

	private void UpdatePlayButtonState() {
		PlayButton.IsEnabled = manifest is not null;
	}

	private void CloseDisplayWindow() {
		foreach (var window in displayWindows.Values)
			window.Close();
		displayWindows.Clear();
	}

	private void CleanupAll() {
		if (audioEngine is not null) {
			audioEngine.PlaybackEnded -= OnPlaybackEnded;
			audioEngine.Dispose();
			audioEngine = null;
		}

		videoEngine?.Dispose();
		videoEngine = null;

		CloseDisplayWindow();

		manifest = null;
		syncCoordinator = null;

		FolderLabel.Text = "No folder loaded";
		VideoLabel.Text = "No video file selected";
		videoFilePath = null;
	}

	/// <inheritdoc />
	protected override void OnClosed(EventArgs e) {
		CleanupAll();
		base.OnClosed(e);
	}
}
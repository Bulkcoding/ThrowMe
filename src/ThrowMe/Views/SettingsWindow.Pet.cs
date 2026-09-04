using Button = System.Windows.Controls.Button;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ThrowMe.Models;
using ThrowMe.Services;
using ThrowMe.Views.Skins;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using UserControl = System.Windows.Controls.UserControl;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace ThrowMe.Views;

/// <summary>설정 창 — 펫 테마(Codex Pet zip) 카드와 CLI 연동 카드.</summary>
public partial class SettingsWindow
{
    private readonly Dictionary<string, Border> _petCards = new(StringComparer.OrdinalIgnoreCase);

    // ── 펫 카드 ─────────────────────────────────────────
    private void BuildPetCards()
    {
        PetCards.Children.Clear();
        _petCards.Clear();
        var packs = PetPackStore.List();
        PetEmptyText.Visibility = packs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var pack in packs)
        {
            var previewHost = new Border
            {
                Width = 96, Height = 82,
                CornerRadius = new CornerRadius(8),
                Background = (Brush)FindResource("WinBg"),
                Child = new Viewbox { Width = 74, Height = 74, Stretch = Stretch.Uniform, Child = new PetSkin(pack, animate: false) { Width = 96, Height = 96 } },
            };
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(previewHost);
            stack.Children.Add(new TextBlock
            {
                Text = pack.DisplayName,
                Foreground = (Brush)FindResource("TextBrush"),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 96,
            });
            var remove = new Button
            {
                Content = "삭제",
                Style = (Style)FindResource("GhostButton"),
                Height = 26, MinWidth = 60, FontSize = 11,
                Margin = new Thickness(0, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            remove.Click += (_, e) => { e.Handled = true; OnRemovePet(pack); };
            stack.Children.Add(remove);

            var card = new Border
            {
                Width = 116,
                Padding = new Thickness(8, 10, 8, 10),
                Margin = new Thickness(0, 0, 12, 12),
                CornerRadius = new CornerRadius(11),
                Background = (Brush)FindResource("CardBg"),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = stack,
                Tag = pack.Id,
            };
            card.MouseLeftButtonUp += (_, _) =>
            {
                _settings.PetId = pack.Id;
                _settings.Skin = SlimeSkinKind.Pet;
            };
            _petCards[pack.Id] = card;
            PetCards.Children.Add(card);
        }
        HighlightPetCards();
    }

    private void HighlightPetCards()
    {
        var accent = (Brush)FindResource("Accent");
        string selected = _settings.Skin == SlimeSkinKind.Pet ? CurrentPetId() : "";
        foreach (var (id, card) in _petCards)
            card.BorderBrush = string.Equals(id, selected, StringComparison.OrdinalIgnoreCase) ? accent : Brushes.Transparent;
    }

    /// <summary>실제로 쓰이는 팩 id(설정이 비어 있거나 없는 팩이면 첫 팩).</summary>
    private string CurrentPetId()
    {
        var pack = PetPackStore.Get(_settings.PetId) ?? PetPackStore.List().FirstOrDefault();
        return pack?.Id ?? "";
    }

    private string CurrentPetName()
    {
        var pack = PetPackStore.Get(_settings.PetId) ?? PetPackStore.List().FirstOrDefault();
        return pack?.DisplayName ?? "펫";
    }

    /// <summary>미리보기용 펫 스킨(팩이 없으면 젤리).</summary>
    private UserControl MakePetPreview()
    {
        var pack = PetPackStore.Get(_settings.PetId) ?? PetPackStore.List().FirstOrDefault();
        return pack == null ? new JellySkin() : new PetSkin(pack, animate: false);
    }

    private void OnImportPet(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Codex Pet zip 선택",
            Filter = "펫 팩 (*.zip)|*.zip|모든 파일|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        var pack = PetPackStore.Import(dlg.FileName, out string error);
        if (pack == null)
        {
            MessageBox.Show(this, "펫 팩을 가져오지 못했습니다.\n" + error, "ThrowMe", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        BuildPetCards();
        _settings.PetId = pack.Id;
        _settings.Skin = SlimeSkinKind.Pet;
        if (_settings.Skin == SlimeSkinKind.Pet) _slime.ReapplySkinPublic(); // 같은 테마여도 새 팩으로 다시 그린다
    }

    private void OnRemovePet(PetPack pack)
    {
        var r = MessageBox.Show(this, $"'{pack.DisplayName}' 펫을 삭제할까요?", "ThrowMe",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;

        bool wasSelected = _settings.Skin == SlimeSkinKind.Pet
            && string.Equals(CurrentPetId(), pack.Id, StringComparison.OrdinalIgnoreCase);
        PetPackStore.Remove(pack.Id);
        if (string.Equals(_settings.PetId, pack.Id, StringComparison.OrdinalIgnoreCase)) _settings.PetId = "";
        BuildPetCards();
        if (wasSelected)
        {
            // 남은 팩이 있으면 그걸로, 없으면 젤리로.
            if (PetPackStore.List().Count == 0) _settings.Skin = SlimeSkinKind.Jelly;
            else _slime.ReapplySkinPublic();
        }
    }

    // ── CLI 연동 카드 ─────────────────────────────────────
    private void UpdateCliCard()
    {
        int port = AppSettings.CliLinkPort;
        // 훅은 수신 토글에 묶여 자동 등록·해제되므로 버튼을 노출하지 않는다.
        InstallHooksBtn.Visibility = Visibility.Collapsed;
        UninstallHooksBtn.Visibility = Visibility.Collapsed;

        string server = !_settings.CliLinkEnabled ? "수신 꺼짐"
            : _slime.CliServerRunning ? $"수신 중 (포트 {port})"
            : $"수신 실패: {_slime.CliLastError ?? "알 수 없음"}";
        string state = _settings.CliLinkEnabled ? $"현재 상태: {StateText(_slime.CurrentAgentState)} · 세션 {_slime.CliSessionCount}개" : "";
        CliStatusText.Text = string.Join("   ·   ", new[] { server, state }.Where(s => s.Length > 0));
    }

    private static string StateText(AgentState s) => SlimeWindow.StateLabel(s);

    private void OnInstallHooks(object sender, RoutedEventArgs e)
    {
        if (!ClaudeHooksInstaller.Install(AppSettings.CliLinkPort, out string error))
        {
            MessageBox.Show(this, "훅을 등록하지 못했습니다.\n" + error, "ThrowMe", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            MessageBox.Show(this,
                $"Claude Code 훅을 등록했습니다.\n({ClaudeHooksInstaller.SettingsPath})\n\n이미 열려 있는 Claude Code 세션은 다시 시작해야 반영됩니다.",
                "ThrowMe", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        UpdateCliCard();
    }

    private void OnUninstallHooks(object sender, RoutedEventArgs e)
    {
        if (!ClaudeHooksInstaller.Uninstall(AppSettings.CliLinkPort, out string error))
            MessageBox.Show(this, "훅을 해제하지 못했습니다.\n" + error, "ThrowMe", MessageBoxButton.OK, MessageBoxImage.Warning);
        UpdateCliCard();
    }
}

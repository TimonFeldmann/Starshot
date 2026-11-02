using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Starshot.Features.Recording;
using Starshot.Frameworks;
using Starshot.Helpers;
using Starshot.Language;

namespace Starshot.Features.Setting;

public sealed partial class RecordingSetting : PageBase
{
    private readonly ILogger<RecordingSetting> _logger = AppConfig.GetLogger<RecordingSetting>();

    /// <summary>录制时是否采集系统声音（WASAPI 回环 → AAC 音轨）。</summary>
    public bool CaptureAudio
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                AppConfig.VideoRecordAudioEnabled = value;
            }
        }
    } = AppConfig.VideoRecordAudioEnabled;

    /// <summary>
    /// HDR (x265) encoder preset index: 0=ultrafast ... 5=medium
    /// (see AppConfig.VideoRecordEncoderPreset).
    /// </summary>
    public int EncoderPresetIndex
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                AppConfig.VideoRecordEncoderPreset = value;
            }
        }
    } = Math.Clamp(AppConfig.VideoRecordEncoderPreset, 0, 5);

    /// <summary>
    /// Target FPS cap free-text input (see AppConfig.VideoRecordTargetFps): empty or 0 =
    /// native display refresh, otherwise the capture pump drops frames before the
    /// readback/convert/encode tail (1..240). Invalid text shows the error hint and is
    /// not persisted. The initializer honors the legacy VideoRecordFpsMode ladder when
    /// TargetFps is unset.
    /// </summary>
    public string TargetFpsText
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                string text = (value ?? string.Empty).Trim();
                if (text.Length == 0)
                {
                    AppConfig.VideoRecordTargetFps = 0;
                    _targetFpsError = false;
                    OnPropertyChanged(nameof(TargetFpsErrorVisible));
                }
                else if (int.TryParse(text, out int fps) && fps is >= 0 and <= 240)
                {
                    AppConfig.VideoRecordTargetFps = fps;
                    _targetFpsError = false;
                    OnPropertyChanged(nameof(TargetFpsErrorVisible));
                }
                else
                {
                    _targetFpsError = true;
                    OnPropertyChanged(nameof(TargetFpsErrorVisible));
                }
            }
        }
    } =
        (
            AppConfig.VideoRecordTargetFps > 0
                ? AppConfig.VideoRecordTargetFps
                : AppConfig.VideoRecordFpsMode switch
                {
                    1 => 60,
                    2 => 30,
                    _ => 0,
                }
        ) switch
        {
            0 => string.Empty,
            int fps => fps.ToString(),
        };

    private bool _targetFpsError;

    public Visibility TargetFpsErrorVisible =>
        _targetFpsError ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Target video bitrate free-text input in Mbps (see AppConfig.VideoRecordBitrateKbps,
    /// stored as kb/s): empty or 0 = off (quality-driven CRF mode), 0.1..1000 = ABR mode.
    /// Invalid text shows the error hint and is not persisted.
    /// </summary>
    public string BitrateText
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                string text = (value ?? string.Empty).Trim();
                if (text.Length == 0)
                {
                    AppConfig.VideoRecordBitrateKbps = 0;
                    _bitrateError = false;
                    OnPropertyChanged(nameof(BitrateErrorVisible));
                }
                else if (
                    double.TryParse(
                        text,
                        NumberStyles.Float,
                        CultureInfo.CurrentCulture,
                        out double mbps
                    ) && (mbps == 0 || (mbps >= 0.1 && mbps <= 1000))
                )
                {
                    AppConfig.VideoRecordBitrateKbps =
                        mbps == 0 ? 0 : (int)Math.Round(mbps * 1000.0);
                    _bitrateError = false;
                    OnPropertyChanged(nameof(BitrateErrorVisible));
                }
                else
                {
                    _bitrateError = true;
                    OnPropertyChanged(nameof(BitrateErrorVisible));
                }
            }
        }
    } =
        AppConfig.VideoRecordBitrateKbps switch
        {
            0 => string.Empty,
            int kbps => string.Format(CultureInfo.CurrentCulture, "{0:0.##}", kbps / 1000.0),
        };

    private bool _bitrateError;

    public Visibility BitrateErrorVisible =>
        _bitrateError ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Post-encode recording: capture writes the converted frames raw to disk and the
    /// encoder runs in the background after stop (see AppConfig.VideoRecordPostEncode).
    /// </summary>
    public bool PostEncode
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                AppConfig.VideoRecordPostEncode = value;
                OnPropertyChanged(nameof(RawBufferVisible));
            }
        }
    } = AppConfig.VideoRecordPostEncode;

    /// <summary>
    /// Fast HDR conversion (Recording settings toggle, default off = bit-exact): a
    /// smaller 80 KB hybrid PQ LUT + reciprocal chroma math. Luma moves at most +-1
    /// code, chroma +-2..3 (inside the HDR suite tolerance bands), in exchange for
    /// slightly lower CPU cost (see AppConfig.VideoRecordFastConvert).
    /// </summary>
    public bool FastConvert
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                AppConfig.VideoRecordFastConvert = value;
            }
        }
    } = AppConfig.VideoRecordFastConvert;

    /// <summary>
    /// HDR convert kernel selection (0 = Auto, 1 = AVX-512, 2 = AVX2 wide, 3 = AVX2
    /// baseline, 4 = AVX2 fast, 5 = scalar; the fixed entries are A/B levers, see
    /// AppConfig.VideoRecordSimdKernel and performance.md).
    /// </summary>
    public int SimdKernelIndex
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                AppConfig.VideoRecordSimdKernel = Math.Clamp(value, 0, 5);
            }
        }
    } = Math.Clamp(AppConfig.VideoRecordSimdKernel, 0, 5);

    /// <summary>
    /// Frame-conversion row-pool worker count (index -&gt; 0 = Auto, 8, 16, 32, 48, 64;
    /// see AppConfig.VideoRecordWorkerCount).
    /// </summary>
    public int WorkerCountIndex
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                AppConfig.VideoRecordWorkerCount = value switch
                {
                    1 => 8,
                    2 => 16,
                    3 => 32,
                    4 => 48,
                    5 => 64,
                    _ => 0,
                };
            }
        }
    } =
        AppConfig.VideoRecordWorkerCount switch
        {
            8 => 1,
            16 => 2,
            32 => 3,
            48 => 4,
            64 => 5,
            _ => 0,
        };

    /// <summary>
    /// The effective raw buffer directory: the user setting when set, otherwise the
    /// Starshot recordings directory. Shown on the folder row; "Change" writes the picked
    /// folder to the setting.
    /// </summary>
    public string RawBufferDir { get; private set; } = string.Empty;

    /// <summary>The raw buffer row only matters in post-encode mode.</summary>
    public Visibility RawBufferVisible => PostEncode ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>录音设备列表：首项恒为"系统默认"，其余为当前活跃的 render 端点。</summary>
    public ObservableCollection<WasapiRenderDevice> AudioDevices { get; } = new();

    /// <summary>当前选中的录音设备；写回 AppConfig（系统默认项 Id 为空 = 跟随 Windows 默认）。</summary>
    public WasapiRenderDevice? SelectedAudioDevice
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                AppConfig.VideoRecordAudioDeviceId = value?.Id;
            }
        }
    }

    public RecordingSetting()
    {
        InitializeComponent();
        RawBufferDir = EffectiveRawBufferDir();
        // 端点枚举走 WASAPI COM（含每个端点的 Activate/GetMixFormat），
        // 放后台线程避免卡 UI，完成后回 UI 线程重建列表
        _ = LoadDevicesAsync();
    }

    /// <summary>
    /// The effective raw buffer directory (the setting, or the recordings directory when
    /// the setting is empty - the same fallback VideoRecordService uses).
    /// </summary>
    private static string EffectiveRawBufferDir()
    {
        string dir = AppConfig.VideoRecordRawBufferDir;
        if (string.IsNullOrWhiteSpace(dir))
        {
            dir = AppConfig.ScreenshotFolder;
            if (string.IsNullOrWhiteSpace(dir))
            {
                dir = Path.Join(AppConfig.UserDataFolder, "Screenshots");
            }
        }
        return dir;
    }

    private async void Button_ChangeRawBufferDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? folder = await FileDialogHelper.PickFolderAsync(this.XamlRoot);
            if (Directory.Exists(folder))
            {
                AppConfig.VideoRecordRawBufferDir = folder;
                RawBufferDir = folder;
                OnPropertyChanged(nameof(RawBufferDir));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pick raw buffer folder failed");
        }
    }

    private async Task LoadDevicesAsync()
    {
        List<WasapiRenderDevice> devices;
        try
        {
            devices = await Task.Run(WasapiDeviceEnumerator.EnumerateRenderDevices);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Enumerate WASAPI render endpoints");
            devices = new List<WasapiRenderDevice>();
        }
        RebuildDeviceList(devices);
    }

    private void RebuildDeviceList(List<WasapiRenderDevice> devices)
    {
        var all = new List<WasapiRenderDevice>(devices.Count + 1)
        {
            // 系统默认项（Id 空 = WasapiLoopbackSource 走 GetDefaultAudioEndpoint）
            new WasapiRenderDevice("", Lang.RecordingSetting_SystemDefault, 0, 0, true),
        };
        all.AddRange(devices);
        AudioDevices.Clear();
        foreach (var d in all)
        {
            AudioDevices.Add(d);
        }
        // 已存的选择还在列表里就恢复，否则（设备被拔/ID 失效）回系统默认
        string? saved = AppConfig.VideoRecordAudioDeviceId;
        SelectedAudioDevice = all.FirstOrDefault(d => d.Id == saved) ?? all[0];
    }

    private void Button_RefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        _ = LoadDevicesAsync();
    }
}

using EarTrumpet.DataModel;
using EarTrumpet.DataModel.WindowsAudio;
using EarTrumpet.Interop.Helpers;
using EarTrumpet.UI.Helpers;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace EarTrumpet.UI.ViewModels
{
    public class EarTrumpetVolumeProfilesSettingsPageViewModel : SettingsPageViewModel
    {
        private readonly AppSettings _settings;
        private readonly VolumeProfileService _profileService;

        public ObservableCollection<VolumeProfileService.VolumeProfile> Profiles => _profileService.Profiles;

        private VolumeProfileService.VolumeProfile _selectedProfile;
        public VolumeProfileService.VolumeProfile SelectedProfile
        {
            get => _selectedProfile;
            set
            {
                _selectedProfile = value;
                RaisePropertyChanged(nameof(SelectedProfile));
                RaisePropertyChanged(nameof(HasSelectedProfile));
                RaisePropertyChanged(nameof(SelectedProfileDetails));
                RaisePropertyChanged(nameof(SelectedProfileApplyAppsOnly));
                UpdateSelectedProfileHotkey();
            }
        }

        public bool HasSelectedProfile => _selectedProfile != null;

        public string SelectedProfileDetails
        {
            get
            {
                if (_selectedProfile == null) return "";
                var slug = VolumeProfileService.GetSlug(_selectedProfile);
                return $"bt {slug} | {QuickTrumpetSummary.DescribeProfile(_selectedProfile)} | {_selectedProfile.CreatedAt}";
            }
        }

        private bool _captureAllDevices;
        public bool CaptureAllDevices
        {
            get => _captureAllDevices;
            set
            {
                _captureAllDevices = value;
                RaisePropertyChanged(nameof(CaptureAllDevices));
            }
        }

        public bool ShowQuickTrumpetConfirmation
        {
            get => _settings.ShowQuickTrumpetConfirmation;
            set
            {
                _settings.ShowQuickTrumpetConfirmation = value;
                RaisePropertyChanged(nameof(ShowQuickTrumpetConfirmation));
            }
        }

        public bool SelectedProfileApplyAppsOnly
        {
            get => _selectedProfile?.ApplyAppsOnly ?? false;
            set
            {
                if (_selectedProfile == null || _selectedProfile.ApplyAppsOnly == value) return;
                _selectedProfile.ApplyAppsOnly = value;
                _profileService.SaveProfile(_selectedProfile);
                RaisePropertyChanged(nameof(SelectedProfileApplyAppsOnly));
                RaisePropertyChanged(nameof(SelectedProfileDetails));
            }
        }

        private HotkeyViewModel _selectedProfileHotkey;
        public HotkeyViewModel SelectedProfileHotkey
        {
            get => _selectedProfileHotkey;
            private set
            {
                _selectedProfileHotkey = value;
                RaisePropertyChanged(nameof(SelectedProfileHotkey));
            }
        }

        private string _newProfileName = "";
        public string NewProfileName
        {
            get => _newProfileName;
            set
            {
                _newProfileName = value;
                RaisePropertyChanged(nameof(NewProfileName));
            }
        }

        // Commands
        public ICommand SaveCurrentCommand { get; }
        public ICommand ApplyProfileCommand { get; }
        public ICommand DeleteProfileCommand { get; }
        public ICommand ExportProfileCommand { get; }
        public ICommand ImportProfileCommand { get; }

        public EarTrumpetVolumeProfilesSettingsPageViewModel(AppSettings settings) : base(null)
        {
            _settings = settings;
            Title = Properties.Resources.QuickTrumpetSettingsPageText;
            Subtitle = Properties.Resources.QuickTrumpetSettingsPageSubtitle;
            Glyph = "\xE9CE"; // Save icon

            _profileService = new VolumeProfileService(settings);

            SaveCurrentCommand = new RelayCommand(SaveCurrentProfile);
            ApplyProfileCommand = new RelayCommand(ApplySelectedProfile);
            DeleteProfileCommand = new RelayCommand(DeleteSelectedProfile);
            ExportProfileCommand = new RelayCommand(ExportSelectedProfile);
            ImportProfileCommand = new RelayCommand(ImportProfileFromFile);
        }

        private DeviceCollectionViewModel GetCollectionViewModel()
        {
            try
            {
                return ((App)Application.Current).CollectionViewModel;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"VolumeProfilesVM: GetCollectionViewModel failed - {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Reloads the presets when another service instance (hotkeys, CLI, settings
        /// import) changed them, keeping the selection by id.
        /// </summary>
        public void RefreshProfiles()
        {
            var selectedId = _selectedProfile?.Id;
            if (_profileService.RefreshIfStale())
            {
                _selectedProfile = _profileService.FindById(selectedId);
                RaisePropertyChanged(nameof(Profiles));
                RaisePropertyChanged(nameof(SelectedProfile));
                RaisePropertyChanged(nameof(HasSelectedProfile));
                RaisePropertyChanged(nameof(SelectedProfileDetails));
                RaisePropertyChanged(nameof(SelectedProfileApplyAppsOnly));
                UpdateSelectedProfileHotkey();
            }
        }

        public VolumeProfileService.VolumeProfile FindProfile(string id) => _profileService.FindById(id);

        /// <summary>Web capture: the user chose exactly what the preset holds.</summary>
        public VolumeProfileService.VolumeProfile CaptureProfile(string name, VolumeProfileService.CaptureOptions options)
        {
            var collection = GetCollectionViewModel();
            if (collection == null) return null;

            name = string.IsNullOrWhiteSpace(name) ? $"Profile {DateTime.Now:yyyy-MM-dd HH:mm}" : name.Trim();
            var profile = _profileService.Capture(name, collection, options);
            _profileService.SaveProfile(profile);
            SelectedProfile = profile;
            RaisePropertyChanged(nameof(Profiles));
            Trace.WriteLine($"VolumeProfilesVM: Captured profile '{name}' ({QuickTrumpetSummary.DescribeProfile(profile)})");
            return profile;
        }

        public void UpdateProfileFromCurrent(VolumeProfileService.VolumeProfile profile)
        {
            var collection = GetCollectionViewModel();
            if (profile == null || collection == null) return;
            _profileService.UpdateFromCurrentState(profile, collection);
            _profileService.CommitEdit(profile);
            RaiseProfileEdited(profile);
        }

        public void SetProfileIncludes(VolumeProfileService.VolumeProfile profile, bool? deviceVolumes, bool? appVolumes, bool? routeApps)
        {
            if (profile == null) return;
            if (deviceVolumes.HasValue) profile.IncludeDeviceVolumes = deviceVolumes.Value;
            if (appVolumes.HasValue) profile.IncludeAppVolumes = appVolumes.Value;
            if (routeApps.HasValue) profile.RouteApps = routeApps.Value;
            _profileService.CommitEdit(profile);
            RaiseProfileEdited(profile);
        }

        public void SetProfileDeviceEntry(VolumeProfileService.VolumeProfile profile, string deviceId, int? volume, bool? muted)
        {
            if (_profileService.SetDeviceEntry(profile, deviceId, volume, muted)) RaiseProfileEdited(profile);
        }

        public void RemoveProfileDeviceEntry(VolumeProfileService.VolumeProfile profile, string deviceId)
        {
            if (_profileService.RemoveDeviceEntry(profile, deviceId)) RaiseProfileEdited(profile);
        }

        public void AddProfileDeviceEntry(VolumeProfileService.VolumeProfile profile, string deviceId)
        {
            if (_profileService.AddDeviceEntry(profile, GetCollectionViewModel(), deviceId)) RaiseProfileEdited(profile);
        }

        public void SetProfileAppEntry(VolumeProfileService.VolumeProfile profile, string appKey, int? volume, bool? muted)
        {
            if (_profileService.SetAppEntry(profile, appKey, volume, muted)) RaiseProfileEdited(profile);
        }

        public void RemoveProfileAppEntry(VolumeProfileService.VolumeProfile profile, string appKey)
        {
            if (_profileService.RemoveAppEntry(profile, appKey)) RaiseProfileEdited(profile);
        }

        public void SetProfileDefaultDevice(VolumeProfileService.VolumeProfile profile, VolumeProfileService.DefaultDeviceRole role, string deviceId)
        {
            if (profile == null) return;
            _profileService.SetDefaultDevice(profile, role, deviceId);
            RaiseProfileEdited(profile);
        }

        private void RaiseProfileEdited(VolumeProfileService.VolumeProfile profile)
        {
            if (ReferenceEquals(profile, _selectedProfile))
            {
                RaisePropertyChanged(nameof(SelectedProfileDetails));
                RaisePropertyChanged(nameof(SelectedProfileApplyAppsOnly));
            }
        }

        private void SaveCurrentProfile()
        {
            var name = string.IsNullOrWhiteSpace(_newProfileName) 
                ? $"Profile {DateTime.Now:yyyy-MM-dd HH:mm}" 
                : _newProfileName.Trim();

            var collection = GetCollectionViewModel();
            if (collection == null) return;

            var profile = _profileService.CaptureCurrentState(
                name,
                collection,
                CaptureAllDevices ? VolumeProfileService.CaptureScope.AllDevices : VolumeProfileService.CaptureScope.CurrentDevice);
            _profileService.SaveProfile(profile);
            SelectedProfile = profile;
            NewProfileName = "";

            RaisePropertyChanged(nameof(Profiles));
            Trace.WriteLine($"VolumeProfilesVM: Saved profile '{name}'");
        }

        private void UpdateSelectedProfileHotkey()
        {
            if (_selectedProfile == null)
            {
                SelectedProfileHotkey = null;
                return;
            }

            if (_selectedProfile.Hotkey == null)
            {
                _selectedProfile.Hotkey = new HotkeyData();
            }

            SelectedProfileHotkey = new HotkeyViewModel(_selectedProfile.Hotkey, newHotkey =>
            {
                _selectedProfile.Hotkey = newHotkey;
                _profileService.SaveProfile(_selectedProfile);
            });
        }

        public void SetProfileHotkey(int index, HotkeyData hotkey)
        {
            var profile = Profiles.ElementAtOrDefault(index);
            if (profile == null)
            {
                return;
            }

            profile.Hotkey = hotkey ?? new HotkeyData();
            _profileService.SaveProfile(profile);

            if (ReferenceEquals(profile, SelectedProfile))
            {
                UpdateSelectedProfileHotkey();
            }
        }

        public void RenameSelectedProfile(string newName)
        {
            if (_selectedProfile == null || string.IsNullOrWhiteSpace(newName)) return;
            var name = newName.Trim();
            if (string.Equals(_selectedProfile.Name, name, StringComparison.Ordinal)) return;

            _profileService.RenameProfile(_selectedProfile, name);
            RaisePropertyChanged(nameof(Profiles));
            Trace.WriteLine($"VolumeProfilesVM: Renamed profile to '{name}'");
        }

        private void ApplySelectedProfile() => ApplySelectedProfile(confirm: true);

        // The web settings window applies presets without a WPF MessageBox; the
        // classic window keeps its confirmation prompt.
        public void ApplySelectedProfile(bool confirm)
        {
            if (_selectedProfile == null) return;

            var collection = GetCollectionViewModel();
            if (collection == null) return;

            var result = !confirm ? MessageBoxResult.Yes : MessageBox.Show(
                $"Apply QuickTrumpet preset \"{_selectedProfile.Name}\"?\n\n" +
                QuickTrumpetSummary.DescribeProfile(_selectedProfile),
                "Apply QuickTrumpet Preset",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                var app = (App)Application.Current;
                var applyResult = _profileService.ApplyProfile(_selectedProfile, collection, app.AudioDeviceManager as IAudioDeviceManagerWindowsAudio);
                app.ShowQuickTrumpetConfirmation(_selectedProfile.Name, applyResult);
            }
        }

        private void DeleteSelectedProfile() => DeleteSelectedProfile(confirm: true);

        // The web settings window confirms inline before sending the action.
        public void DeleteSelectedProfile(bool confirm)
        {
            if (_selectedProfile == null) return;

            var result = !confirm ? MessageBoxResult.Yes : MessageBox.Show(
                $"Delete QuickTrumpet preset \"{_selectedProfile.Name}\"?",
                "Delete QuickTrumpet Preset",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                var toDelete = _selectedProfile;
                SelectedProfile = null;
                _profileService.DeleteProfile(toDelete);
                RaisePropertyChanged(nameof(Profiles));
            }
        }

        private void ExportSelectedProfile()
        {
            if (_selectedProfile == null) return;

            try
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Export QuickTrumpet Preset",
                    Filter = "BetterTrumpet Profile (*.btprofile)|*.btprofile|JSON Files (*.json)|*.json",
                    DefaultExt = ".btprofile",
                    FileName = SanitizeFileName(_selectedProfile.Name)
                };

                if (dlg.ShowDialog() == true)
                {
                    var json = _profileService.ExportProfile(_selectedProfile);
                    System.IO.File.WriteAllText(dlg.FileName, json, System.Text.Encoding.UTF8);
                    Trace.WriteLine($"VolumeProfilesVM: Exported to {dlg.FileName}");
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"VolumeProfilesVM: Export failed - {ex.Message}");
            }
        }

        private void ImportProfileFromFile()
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Import Volume Profile",
                    Filter = "BetterTrumpet Profile (*.btprofile)|*.btprofile|JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                    DefaultExt = ".btprofile"
                };

                if (dlg.ShowDialog() == true)
                {
                    var json = System.IO.File.ReadAllText(dlg.FileName, System.Text.Encoding.UTF8);
                    var profile = _profileService.ImportProfile(json);
                    if (profile != null)
                    {
                        SelectedProfile = profile;
                        RaisePropertyChanged(nameof(Profiles));
                        Trace.WriteLine($"VolumeProfilesVM: Imported from {dlg.FileName}");
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"VolumeProfilesVM: Import failed - {ex.Message}");
            }
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "profile";
            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }
    }
}

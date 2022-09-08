using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Timers;
using HomeSeer.Jui.Views;
using HomeSeer.PluginSdk;
using HomeSeer.PluginSdk.Devices;
using HomeSeer.PluginSdk.Logging;
using HSPI_IdleLightColors.Enums;
using HSPI_IdleLightColors.Structs;

namespace HSPI_IdleLightColors
{
	// ReSharper disable once InconsistentNaming
	public class HSPI : AbstractPlugin {
		public override string Name { get; } = "HS-WD200 Idle Light Colors";
		public override string Id { get; } = "WD200 Idle Light Colors";

		private WD200NormalModeColor _offColor;
		private WD200NormalModeColor _onColor;

		private Dictionary<int, DimmerDevice> _dimmersByRef;
		private bool _haveDoneInitialUpdate;
		private ZwavePluginType _zwavePluginType = ZwavePluginType.Unknown;
		private Timer _updateAllDimmersTimer = null;
		private bool _debugLogging = false;

		private const int MFG_ID_HOMESEER_TECHNOLOGIES = 0x000c;

		public HSPI() {
			#if DEBUG
			LogDebug = true;
			#endif
		}

		protected override void Initialize() {
			WriteLog(ELogType.Debug, "Initialize");
			
			AnalyticsClient analytics = new AnalyticsClient(this, HomeSeerSystem);

			_dimmersByRef = new Dictionary<int, DimmerDevice>();
			_haveDoneInitialUpdate = false;

			Dictionary<byte, DimmerDevice> dict = new Dictionary<byte, DimmerDevice>();
			foreach (int featureRef in HomeSeerSystem.GetAllFeatureRefs()) {
				HsFeature feature = HomeSeerSystem.GetFeatureByRef(featureRef);
				if (feature.Interface != "Z-Wave") {
					continue;
				}
				
				string[] addressParts = feature.Address.Split('-');
				byte nodeId = byte.Parse(addressParts[1]);
				if (dict.ContainsKey(nodeId)) {
					continue;
				}

				if (DeviceIsDimmer(feature)) {
					DimmerDevice dimmerDevice = new DimmerDevice {
						HomeID = addressParts[0],
						NodeID = nodeId,
						SwitchMultiLevelDeviceRef = featureRef
					};

					dict[nodeId] = dimmerDevice;
					_dimmersByRef[dimmerDevice.SwitchMultiLevelDeviceRef] = dimmerDevice;
				}
			}

#pragma warning disable CS0618
			HomeSeerSystem.RegisterEventCB(Constants.HSEvent.VALUE_SET, Id);
			HomeSeerSystem.RegisterEventCB(Constants.HSEvent.VALUE_CHANGE, Id);
#pragma warning restore CS0618

			// Build the settings page
			PageFactory settingsPageFactory = PageFactory
				.CreateSettingsPage("IdleLightColorsSettings", "HS-WD200+ Idle Light Colors Settings")
				.WithLabel("plugin_status", "Status (refresh to update)", "x")
				.WithDropDownSelectList("active_color", "Normal Mode Active Color", GetColorList())
				.WithDropDownSelectList("idle_color", "Normal Mode Idle Color", GetColorList())
				.WithGroup("debug_group", "<hr>", new AbstractView[] {
					new LabelView("debug_donate_link", "Fund Future Development", "This plugin is and always will be free.<br /><a href=\"https://github.com/sponsors/DoctorMcKay\" target=\"_blank\">Please consider donating to fund future development.</a>"),
					new LabelView("debug_system_id", "System ID (include this with any support requests)", analytics.CustomSystemId),
#if DEBUG
					new LabelView("debug_log", "Enable Debug Logging", "ON - DEBUG BUILD")
#else
					new ToggleView("debug_log", "Enable Debug Logging")
#endif
				});

			Settings.Add(settingsPageFactory.Page);

			string defaultIdle = ((int) WD200NormalModeColor.Blue).ToString();
			string defaultActive = ((int) WD200NormalModeColor.White).ToString();
			
			string idleColorStr = HomeSeerSystem.GetINISetting("Colors", "idle_color", defaultIdle, Id);
			string activeColorStr = HomeSeerSystem.GetINISetting("Colors", "active_color", defaultActive, Id);

			_offColor = (WD200NormalModeColor) int.Parse(idleColorStr);
			_onColor = (WD200NormalModeColor) int.Parse(activeColorStr);

			WriteLog(ELogType.Info, string.Format(
				"Init complete. Active color: {0}. Idle color: {1}. Found {2} dimmers with node IDs: {3}",
				_onColor,
				_offColor,
				_dimmersByRef.Keys.Count,
				string.Join(", ", _dimmersByRef.Values.Select(dimmerDevice => dimmerDevice.NodeID))
			));
			
			analytics.ReportIn(5000);
		}

		protected override void OnSettingsLoad() {
			// Called when the settings page is loaded. Use to pre-fill the inputs.
			string statusText = Status.Status.ToString().ToUpper();
			if (Status.StatusText.Length > 0) {
				statusText += ": " + Status.StatusText;
			}
			((LabelView) Settings.Pages[0].GetViewById("plugin_status")).Value = statusText;
			((SelectListView) Settings.Pages[0].GetViewById("idle_color")).Selection = (int) _offColor;
			((SelectListView) Settings.Pages[0].GetViewById("active_color")).Selection = (int) _onColor;
		}

		protected override bool OnSettingChange(string pageId, AbstractView currentView, AbstractView changedView) {
			WriteLog(ELogType.Debug, $"Request to save setting {currentView.Id} on page {pageId}");

			if (pageId != "IdleLightColorsSettings") {
				WriteLog(ELogType.Warning, $"Request to save settings on unknown page {pageId}!");
				return true;
			}

			switch (currentView.Id) {
				case "idle_color":
					_offColor = (WD200NormalModeColor) ((SelectListView) changedView).Selection;
					HomeSeerSystem.SaveINISetting("Colors", "idle_color", ((int) _offColor).ToString(), SettingsFileName);
					WriteLog(ELogType.Info, $"WD200 idle color changed to {_offColor}");
					QueueUpdateAllDimmers();
					return true;
				
				case "active_color":
					_onColor = (WD200NormalModeColor) ((SelectListView) changedView).Selection;
					HomeSeerSystem.SaveINISetting("Colors", "active_color", ((int) _onColor).ToString(), SettingsFileName);
					WriteLog(ELogType.Info, $"WD200 active color changed to {_onColor}");
					QueueUpdateAllDimmers();
					return true;
				
				case "debug_log":
					_debugLogging = changedView.GetStringValue() == "True";
					return true;
			}
			
			WriteLog(ELogType.Info, $"Request to save unknown setting {currentView.Id}");
			return false;
		}

		private static List<string> GetColorList() {
			return (from int i in Enum.GetValues(typeof(WD200NormalModeColor)) select ((WD200NormalModeColor) i).ToString()).ToList();
		}

		private static bool DeviceIsDimmer(HsFeature feature) {
			PlugExtraData extraData = feature.PlugExtraData;

			try {
				int.TryParse(extraData.GetNamed("manufacturer_id"), out int manufacturerId);
				int.TryParse(extraData.GetNamed("manufacturer_prod_id"), out int prodId);
				int.TryParse(extraData.GetNamed("manufacturer_prod_type"), out int prodType);
				int.TryParse(extraData.GetNamed("relationship"), out int relationship);
				int.TryParse(extraData.GetNamed("commandclass"), out int commandClass);
				
				// Product ID: 0x3036 = WD200; 0x4036 = WX300 in dimmer mode; 0x4037 = WX300 in binary switch mode
				// Command Class: 0x25 is binary switch, for WX300. 0x26 is switch multilevel - http://wiki.micasaverde.com/index.php/ZWave_Command_Classes
				return manufacturerId == MFG_ID_HOMESEER_TECHNOLOGIES
				       && prodType == 0x4447
				       && prodId is 0x3036 or 0x4036 or 0x4037
				       && relationship == 4
				       && commandClass is 0x25 or 0x26;
			} catch (Exception) {
				// A key wasn't found, which means this isn't a dimmer device
				return false;
			}
		}

		private string SetDeviceNormalModeColor(string homeId, byte nodeId, WD200NormalModeColor color) {
			object result = ZWavePluginFunction("SetDeviceParameterValue", new object[] {
				homeId,
				nodeId,
				(byte) WD200ConfigParam.NormalModeLedColor,
				(byte) 1, // length of value in bytes
				(int) color
			});
			
			if (_zwavePluginType == ZwavePluginType.LegacyPreSetDeviceParameterValue) {
				HSPI_ZWave.HSPI.ConfigResult enumResult = (HSPI_ZWave.HSPI.ConfigResult) result;
				return enumResult.ToString();
			}
			
			return (string) result;
		}

#pragma warning disable CS0618
		public override void HsEvent(Constants.HSEvent eventType, object[] parameters) {
			try {
				if (
					eventType != Constants.HSEvent.VALUE_SET &&
					eventType != Constants.HSEvent.VALUE_CHANGE
				) {
					WriteLog(ELogType.Warning, "Got unknown HSEvent type " + eventType);
					return;
				}

				int devRef = (int) parameters[4];
				double newValue = (double) parameters[2];

				if (!_dimmersByRef.ContainsKey(devRef)) {
					return;
				}

				WriteLog(ELogType.Debug, $"Dimmer {devRef} was set to {newValue}.");

				if (!_haveDoneInitialUpdate) {
					// We want to delay this until we've confirmed that we received a Z-Wave update, since now we know
					// that Z-Wave is up and running
					_haveDoneInitialUpdate = true;
					UpdateAllDimmers();
				} else {
					UpdateDimmerForStatus(_dimmersByRef[devRef], newValue);
				}
			} catch (Exception ex) {
				WriteLog(ELogType.Error, "Exception in HSEvent: " + ex.Message);
				Console.WriteLine(ex);
			}
		}
#pragma warning restore CS0618

		private void QueueUpdateAllDimmers() {
			if (!_haveDoneInitialUpdate) {
				// If we haven't done our initial update yet, do nothing
				return;
			}
			
			_updateAllDimmersTimer?.Stop();
			_updateAllDimmersTimer?.Dispose();

			_updateAllDimmersTimer = new Timer(1000) {
				Enabled = true,
				AutoReset = false
			};

			_updateAllDimmersTimer.Elapsed += (_, _) => {
				_updateAllDimmersTimer.Dispose();
				_updateAllDimmersTimer = null;

				UpdateAllDimmers();
			};
		}

		private void UpdateAllDimmers() {
			WriteLog(ELogType.Info, "Running startup update for all dimmers");
			
			foreach (DimmerDevice dimmerDevice in _dimmersByRef.Values) {
				UpdateDimmerForStatus(dimmerDevice, HomeSeerSystem.GetFeatureByRef(dimmerDevice.SwitchMultiLevelDeviceRef).Value);
			}
		}

		private void UpdateDimmerForStatus(DimmerDevice dimmerDevice, double value) {
			WD200NormalModeColor newColor = Math.Abs(value) < 0.1 ? _offColor : _onColor;
			string result = SetDeviceNormalModeColor(dimmerDevice.HomeID, dimmerDevice.NodeID, newColor);
			WriteLog(ELogType.Info, string.Format(
				"Setting normal mode color for device {0} (node ID {1}) to {2}; result: {3}",
				dimmerDevice.SwitchMultiLevelDeviceRef,
				dimmerDevice.NodeID,
				newColor,
				result
			));
		}
		
		private object ZWavePluginFunction(string functionName, object[] param) {
			if (_zwavePluginType == ZwavePluginType.Unknown) {
				string[] pluginVersion = HomeSeerSystem.GetPluginVersionByName("Z-Wave").Split('.');
				switch (int.Parse(pluginVersion[0])) {
					case 3:
						_zwavePluginType = ZwavePluginType.Legacy;
						break;
					
					case 4:
						_zwavePluginType = ZwavePluginType.HS4Native;
						break;
					
					default:
						Status = PluginStatus.Fatal("Couldn't detect Z-Wave plugin");
						return null;
				}
				
				WriteLog(ELogType.Debug, $"Detected Z-Wave plugin type: {_zwavePluginType}");
			}
			
			// At some point c. 3.0.9.0, a new function SetDeviceParameterValue was added which wraps Configuration_Set
			// and returns a string value rather than an enum value. This solves issues present when unserializing
			// a value from another assembly, especially if that other assembly doesn't live in the same directory
			// as the executing assembly. To maintain compatibility with older Z-Wave plugin versions, we want to detect
			// when SetDeviceParameterValue failed (via a null return value) and fall back to using Configuration_Set.

			switch (_zwavePluginType) {
				case ZwavePluginType.Legacy:
					object result = HomeSeerSystem.LegacyPluginFunction("Z-Wave", "", functionName, param);
					
					if (functionName == "SetDeviceParameterValue" && result == null) {
						_zwavePluginType = ZwavePluginType.LegacyPreSetDeviceParameterValue;
						WriteLog(ELogType.Debug, $"Detected Z-Wave plugin type: {_zwavePluginType}");
						return ZWavePluginFunction(functionName, param);
					}

					return result;

				case ZwavePluginType.LegacyPreSetDeviceParameterValue:
					if (functionName == "SetDeviceParameterValue") {
						functionName = "Configuration_Set";
					}

					// I haven't actually gotten this to work, but for some reason my dev environment doesn't work with
					// the old build of this plugin even though it works perfectly fine on my production system. So I
					// *suppose* this should work. Anyone who has issues with this should update the Z-Wave plugin anyway.
					return HomeSeerSystem.LegacyPluginFunction("Z-Wave", "", functionName, param);

				case ZwavePluginType.HS4Native:
					return HomeSeerSystem.PluginFunction("Z-Wave", functionName, param);
				
				default:
					return null;
			}
		}

		protected override void BeforeReturnStatus() { }

		public void WriteLog(ELogType logType, string message, [CallerLineNumber] int lineNumber = 0, [CallerMemberName] string caller = null) {
#if DEBUG
			bool isDebugMode = true;

			// Prepend calling function and line number
			message = $"[{caller}:{lineNumber}] {message}";

			// Also print to console in debug builds
			string type = logType.ToString().ToLower();
			Console.WriteLine($"[{type}] {message}");
#else
			if (logType == ELogType.Trace) {
				// Don't record Trace events in production builds even if debug logging is enabled
				return;
			}

			bool isDebugMode = _debugLogging;
#endif

			if (logType <= ELogType.Debug && !isDebugMode) {
				return;
			}

			HomeSeerSystem.WriteLog(logType, message, Name);
		}
	}
}

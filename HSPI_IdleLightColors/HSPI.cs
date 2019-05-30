using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using HomeSeerAPI;
using HSPI_IdleLightColors.Enums;
using HSPI_IdleLightColors.Structs;
using Scheduler;
using Scheduler.Classes;

namespace HSPI_IdleLightColors
{
	// ReSharper disable once InconsistentNaming
	public class HSPI : HspiBase
	{
		public const string PLUGIN_NAME = "HS-WD200+ Idle Light Colors";

		private const WD200NormalModeColor OFF_COLOR = WD200NormalModeColor.Blue;
		private const WD200NormalModeColor ON_COLOR = WD200NormalModeColor.White;

		private Dictionary<int, DimmerDevice> dimmersByRef;

		public HSPI() {
			Name = PLUGIN_NAME;
			PluginIsFree = true;
		}

		public override string InitIO(string port) {
			Program.WriteLog(LogType.Verbose, "InitIO");

			dimmersByRef = new Dictionary<int, DimmerDevice>();
			
			Dictionary<byte, DimmerDevice> dict = new Dictionary<byte, DimmerDevice>();
			clsDeviceEnumeration enumerator = (clsDeviceEnumeration) hs.GetDeviceEnumerator();
			do {
				DeviceClass device = enumerator.GetNext();
				if (device != null) {
					if (device.get_Interface(hs) != "Z-Wave") {
						continue;
					}

					// It's a Z-Wave device
					PlugExtraData.clsPlugExtraData extraData = device.get_PlugExtraData_Get(hs);
					byte? nodeId = (byte?) extraData.GetNamed("node_id");
					if (nodeId == null || dict.ContainsKey((byte) nodeId)) {
						continue;
					}

					if (deviceIsDimmer(extraData)) {
						DimmerDevice dimmerDevice = new DimmerDevice {
							HomeID = (string) extraData.GetNamed("homeid"),
							NodeID = (byte) nodeId,
							SwitchMultiLevelDeviceRef = device.get_Ref(hs)
						};

						dict[(byte) nodeId] = dimmerDevice;
						dimmersByRef[dimmerDevice.SwitchMultiLevelDeviceRef] = dimmerDevice;
						
						// Initial update
						updateDimmerForStatus(dimmerDevice, device.get_devValue(hs));
					}
				}
			} while (!enumerator.Finished);
			
			callbacks.RegisterEventCB(HomeSeerAPI.Enums.HSEvent.VALUE_SET, Name, InstanceFriendlyName());
			callbacks.RegisterEventCB(HomeSeerAPI.Enums.HSEvent.VALUE_CHANGE, Name, InstanceFriendlyName());

			Program.WriteLog(LogType.Info, "Init complete");

			return "";
		}
		
		private bool deviceIsDimmer(PlugExtraData.clsPlugExtraData extraData) {
			int? manufacturerId = (int?) extraData.GetNamed("manufacturer_id");
			UInt16? prodId = (UInt16?) extraData.GetNamed("manufacturer_prod_id");
			UInt16? prodType = (UInt16?) extraData.GetNamed("manufacturer_prod_type");
			int? relationship = (int?) extraData.GetNamed("relationship");
			byte? commandClass = (byte?) extraData.GetNamed("commandclass");

			return manufacturerId == 12 && prodId == 12342 && prodType == 17479 && relationship == 4 && commandClass == 38;
		}

		private HSPI_ZWave.HSPI.ConfigResult setDeviceNormalModeColor(string homeID, byte nodeID, WD200NormalModeColor color) {
			return (HSPI_ZWave.HSPI.ConfigResult) hs.PluginFunction("Z-Wave", "", "Configuration_Set", new object[] {
				homeID,
				nodeID,
				(byte) WD200ConfigParam.NormalModeLedColor,
				(byte) 1, // length of value in bytes, I assume
				(int) color
			});
		}

		public override void HSEvent(HomeSeerAPI.Enums.HSEvent eventType, object[] parameters) {
			try {
				if (
					eventType != HomeSeerAPI.Enums.HSEvent.VALUE_SET &&
					eventType != HomeSeerAPI.Enums.HSEvent.VALUE_CHANGE
				) {
					Program.WriteLog(LogType.Warn, "Got unknown HSEvent type " + eventType);
					return;
				}

				int devRef = (int) parameters[4];
				double newValue = (double) parameters[2];

				if (!dimmersByRef.ContainsKey(devRef)) {
					return;
				}

				Program.WriteLog(LogType.Debug, string.Format("Dimmer {0} was set to {1}.", devRef, newValue));
				
				updateDimmerForStatus(dimmersByRef[devRef], newValue);
			} catch (Exception ex) {
				Program.WriteLog(LogType.Error, "Exception in HSEvent: " + ex.Message);
				Console.WriteLine(ex);
			}
		}

		private void updateDimmerForStatus(DimmerDevice dimmerDevice, double value) {
			WD200NormalModeColor newColor = Math.Abs(value) < 0.1 ? OFF_COLOR : ON_COLOR;
			HSPI_ZWave.HSPI.ConfigResult result = setDeviceNormalModeColor(dimmerDevice.HomeID, dimmerDevice.NodeID, newColor);
			Program.WriteLog(LogType.Info, string.Format(
				"Setting normal mode color for device {0} (node ID {1}) to {2}; result: {3}",
				dimmerDevice.SwitchMultiLevelDeviceRef,
				dimmerDevice.NodeID,
				newColor,
				result
			));}
	}
}

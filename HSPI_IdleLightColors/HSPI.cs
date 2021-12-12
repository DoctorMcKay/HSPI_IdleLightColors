using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using System.Web;
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
		public const string PLUGIN_NAME = "HS-WD200 Idle Light Colors";

		private WD200NormalModeColor offColor;
		private WD200NormalModeColor onColor;

		private Dictionary<int, DimmerDevice> dimmersByRef;
		private bool haveDoneInitialUpdate;

		private const int MFG_ID_HOMESEER_TECHNOLOGIES = 0x000c;

		public HSPI() {
			Name = PLUGIN_NAME;
			PluginIsFree = true;
		}

		public override string InitIO(string port) {
			Program.WriteLog(LogType.Verbose, "InitIO");

			dimmersByRef = new Dictionary<int, DimmerDevice>();
			haveDoneInitialUpdate = false;
			
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
					string[] addressParts = device.get_Address(hs).Split('-');
					byte nodeId = byte.Parse(addressParts[1]);
					if (dict.ContainsKey(nodeId)) {
						continue;
					}

					if (DeviceIsDimmer(extraData)) {
						DimmerDevice dimmerDevice = new DimmerDevice {
							HomeID = addressParts[0],
							NodeID = nodeId,
							SwitchMultiLevelDeviceRef = device.get_Ref(hs)
						};

						dict[nodeId] = dimmerDevice;
						dimmersByRef[dimmerDevice.SwitchMultiLevelDeviceRef] = dimmerDevice;
					}
				}
			} while (!enumerator.Finished);

			callbacks.RegisterEventCB(HomeSeerAPI.Enums.HSEvent.VALUE_SET, Name, InstanceFriendlyName());
			callbacks.RegisterEventCB(HomeSeerAPI.Enums.HSEvent.VALUE_CHANGE, Name, InstanceFriendlyName());

			hs.RegisterPage("IdleLightColorsSettings", Name, InstanceFriendlyName());
			WebPageDesc configLink = new WebPageDesc {
				plugInName = Name,
				link = "IdleLightColorsSettings",
				linktext = "Settings",
				order = 1,
				page_title = "HS-WD200+ Idle Light Colors Settings",
				plugInInstance = InstanceFriendlyName()
			};
			callbacks.RegisterConfigLink(configLink);
			callbacks.RegisterLink(configLink);

			offColor = (WD200NormalModeColor) int.Parse(hs.GetINISetting("Colors", "idle_color",
				((int) WD200NormalModeColor.Blue).ToString(), IniFilename));
			onColor = (WD200NormalModeColor) int.Parse(hs.GetINISetting("Colors", "active_color",
				((int) WD200NormalModeColor.White).ToString(), IniFilename));

			Program.WriteLog(LogType.Info, string.Format(
				"Init complete. Active color: {0}. Idle color: {1}. Found {2} dimmers with node IDs: {3}",
				onColor,
				offColor,
				dimmersByRef.Keys.Count,
				string.Join(", ", dimmersByRef.Values.Select(dimmerDevice => dimmerDevice.NodeID))
			));

			return "";
		}

		public override string GetPagePlugin(string pageName, string user, int userRights, string queryString) {
			Program.WriteLog(LogType.Verbose, $"Requested page name {pageName} by user {user} with rights {userRights}");

			switch (pageName) {
				case "IdleLightColorsSettings":
					return BuildSettingsPage(user, userRights, queryString);
			}

			return "";
		}

		private string BuildSettingsPage(string user, int userRights, string queryString, string messageBox = null, string messageBoxClass = null) {
			string pageName = "IdleLightColorsSettings";
			PageBuilderAndMenu.clsPageBuilder builder = new PageBuilderAndMenu.clsPageBuilder(pageName);
			if ((userRights & 2) != 2) {
				// User is not an admin
				builder.reset();
				builder.AddHeader(hs.GetPageHeader(pageName, "HS-WD200+ Idle Light Colors Settings", "", "", false, true));
				builder.AddBody("<p><strong>Access Denied:</strong> You are not an administrative user.</p>");
				builder.AddFooter(hs.GetPageFooter());
				builder.suppressDefaultFooter = true;

				return builder.BuildPage();
			}

			StringBuilder sb = new StringBuilder();
			sb.Append(
				"<p>The active color will be used when an HS-WD200+ dimmer is in normal mode and is on (at any level).<br />The idle color will be used when an HS-WD200+ dimmer is in normal mode and is off.</p>");
			sb.Append("<p>Status mode will override these colors, as usual.</p>");
			
			sb.Append(PageBuilderAndMenu.clsPageBuilder.FormStart("ils_config_form", "ils_config_form", "post"));
			sb.Append("<table width=\"1000px\" cellspacing=\"0\"><tr><td class=\"tableheader\" colspan=\"3\">Settings</td></tr>");
			
			sb.Append("<tr><td class=\"tablecell\" style=\"width:200px\" align=\"left\">Normal Mode Active Color:</td>");
			sb.Append("<td class=\"tablecell\">");
			clsJQuery.jqDropList dropdown = new clsJQuery.jqDropList("ActiveColor", pageName, true);
			BuildColorDropdown(pageName, dropdown, onColor);
			sb.Append(dropdown.Build());
			sb.Append("</td></tr>");
			
			sb.Append("<tr><td class=\"tablecell\" style=\"width:200px\" align=\"left\">Normal Mode Idle Color:</td>");
			sb.Append("<td class=\"tablecell\">");
			dropdown = new clsJQuery.jqDropList("IdleColor", pageName, true);
			BuildColorDropdown(pageName, dropdown, offColor);
			sb.Append(dropdown.Build());
			sb.Append("</td></tr>");

			sb.Append("</table>");
			
			clsJQuery.jqButton doneBtn = new clsJQuery.jqButton("DoneBtn", "Done", pageName, false);
			doneBtn.url = "/";
			sb.Append("<br />");
			sb.Append(doneBtn.Build());
			sb.Append("<br /><br />");
			
			builder.reset();
			builder.AddHeader(hs.GetPageHeader(pageName, "HS-WD200+ Idle Light Colors Settings", "", "", false, true));
			builder.AddBody(sb.ToString());
			builder.AddFooter(hs.GetPageFooter());
			builder.suppressDefaultFooter = true;

			return builder.BuildPage();
		}

		public override string PostBackProc(string page, string data, string user, int userRights) {
			Program.WriteLog(LogType.Debug, $"PostBackProc page name {page} by user {user} with rights {userRights}");
			if (page != "IdleLightColorsSettings") {
				return "Unknown page " + page;
			}

			if ((userRights & 2) != 2) {
				return "Access denied: you are not an administrative user.";
			}

			NameValueCollection postData = HttpUtility.ParseQueryString(data);

			string activeColor = postData.Get("ActiveColor");
			string idleColor = postData.Get("IdleColor");
			hs.SaveINISetting("Colors", "active_color", activeColor, IniFilename);
			hs.SaveINISetting("Colors", "idle_color", idleColor, IniFilename);
			onColor = (WD200NormalModeColor) int.Parse(activeColor);
			offColor = (WD200NormalModeColor) int.Parse(idleColor);

			if (haveDoneInitialUpdate) {
				UpdateAllDimmers();
			}

			return "";
		}

		private static void BuildColorDropdown(string pageName, clsJQuery.jqDropList dropdown, WD200NormalModeColor selectedColor) {
			foreach (int i in Enum.GetValues(typeof(WD200NormalModeColor))) {
				dropdown.AddItem(((WD200NormalModeColor) i).ToString(), i.ToString(), (int) selectedColor == i);
			}
		}
		
		private static bool DeviceIsDimmer(PlugExtraData.clsPlugExtraData extraData) {
			int? manufacturerId = (int?) extraData.GetNamed("manufacturer_id");
			ushort? prodId = (ushort?) extraData.GetNamed("manufacturer_prod_id");
			ushort? prodType = (ushort?) extraData.GetNamed("manufacturer_prod_type");
			int? relationship = (int?) extraData.GetNamed("relationship");
			byte? commandClass = (byte?) extraData.GetNamed("commandclass");

			// 0x3036 = WD200; 0x4036 = WX300 in dimmer mode; 0x4037 = WX300 in binary switch mode
			return manufacturerId == MFG_ID_HOMESEER_TECHNOLOGIES
			       && prodType == 0x4447
			       && (prodId == 0x3036 || prodId == 0x4036 || prodId == 0x4037)
			       && relationship == 4
			       && commandClass == 38;
		}

		private HSPI_ZWave.HSPI.ConfigResult SetDeviceNormalModeColor(string homeID, byte nodeID, WD200NormalModeColor color) {
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

				Program.WriteLog(LogType.Debug, $"Dimmer {devRef} was set to {newValue}.");

				if (!haveDoneInitialUpdate) {
					// We want to delay this until we've confirmed that we received a Z-Wave update, since now we know
					// that Z-Wave is up and running
					haveDoneInitialUpdate = true;
					UpdateAllDimmers();
				} else {
					UpdateDimmerForStatus(dimmersByRef[devRef], newValue);
				}
			} catch (Exception ex) {
				Program.WriteLog(LogType.Error, "Exception in HSEvent: " + ex.Message);
				Console.WriteLine(ex);
			}
		}

		private void UpdateAllDimmers() {
			foreach (DimmerDevice dimmerDevice in dimmersByRef.Values) {
				Program.WriteLog(LogType.Info, "Running startup update for all dimmers");
				UpdateDimmerForStatus(dimmerDevice, hs.DeviceValueEx(dimmerDevice.SwitchMultiLevelDeviceRef));
			}
		}

		private void UpdateDimmerForStatus(DimmerDevice dimmerDevice, double value) {
			WD200NormalModeColor newColor = Math.Abs(value) < 0.1 ? offColor : onColor;
			HSPI_ZWave.HSPI.ConfigResult result = SetDeviceNormalModeColor(dimmerDevice.HomeID, dimmerDevice.NodeID, newColor);
			Program.WriteLog(LogType.Info, string.Format(
				"Setting normal mode color for device {0} (node ID {1}) to {2}; result: {3}",
				dimmerDevice.SwitchMultiLevelDeviceRef,
				dimmerDevice.NodeID,
				newColor,
				result
			));}
	}
}

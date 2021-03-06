/*
* Copyright (C) 2009 The Android Open Source Project
*
* Licensed under the Apache License, Version 2.0 (the "License");
* you may not use this file except in compliance with the License.
* You may obtain a copy of the License at
*
* http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Android.Bluetooth;
using Android.Content;
using Android.OS;
using Android.Support.V7.App;
using Android.Views;
using Android.Widget;
using EdiabasLib;
using Java.Util;
using Android.Text.Method;

namespace BmwDeepObd
{

    /// <summary>
    /// This Activity appears as a dialog. It lists any paired devices and
    /// devices detected in the area after discovery. When a device is chosen
    /// by the user, the MAC address of the device is sent back to the parent
    /// Activity in the result Intent.
    /// </summary>
    [Android.App.Activity (Label = "@string/select_device",
            ConfigurationChanges = Android.Content.PM.ConfigChanges.KeyboardHidden |
                Android.Content.PM.ConfigChanges.Orientation |
                Android.Content.PM.ConfigChanges.ScreenSize)]
    public class DeviceListActivity : AppCompatActivity
    {
        enum AdapterType
        {
            ConnectionFailed,   // connection to adapter failed
            Unknown,            // unknown adapter
            Elm327,             // ELM327
            Elm327Invalid,      // ELM327 invalid type
            Elm327Fake21,       // ELM327 fake 2.1 version
            Custom,             // custom adapter
            CustomUpdate,       // custom adapter with firmware update
            EchoOnly,           // only echo response
        }

        enum BtOperation
        {
            SelectAdapter,      // select the adapter
            ConnectObd,         // connect device as OBD
            ConnectPhone,       // connect device as phone
            DisconnectPhone,    // dosconnect phone
            DeleteDevice,       // delete device
        }

        private static readonly UUID SppUuid = UUID.FromString("00001101-0000-1000-8000-00805F9B34FB");
        private static readonly long TickResolMs = Stopwatch.Frequency / 1000;
        private const int ResponseTimeout = 1000;

        // Return Intent extra
        public const string ExtraAppDataDir = "app_data_dir";
        public const string ExtraDeviceName = "device_name";
        public const string ExtraDeviceAddress = "device_address";
        public const string ExtraCallAdapterConfig = "adapter_configuration";

        // Member fields
        private BluetoothAdapter _btAdapter;
        private static ArrayAdapter<string> _pairedDevicesArrayAdapter;
        private static ArrayAdapter<string> _newDevicesArrayAdapter;
        private Receiver _receiver;
        private AlertDialog _altertInfoDialog;
        private Button _scanButton;
        private ActivityCommon _activityCommon;
        private string _appDataDir;
        private readonly StringBuilder _sbLog = new StringBuilder();
        private readonly AutoResetEvent _connectedEvent = new AutoResetEvent(false);
        private volatile string _connectDeviceAddress = string.Empty;
        private volatile bool _deviceConnected;

        protected override void OnCreate (Bundle savedInstanceState)
        {
            base.OnCreate (savedInstanceState);

            SupportActionBar.SetHomeButtonEnabled(true);
            SupportActionBar.SetDisplayShowHomeEnabled(true);
            SupportActionBar.SetDisplayHomeAsUpEnabled(true);
            // Setup the window
            SetContentView(Resource.Layout.device_list);

            // Set result CANCELED incase the user backs out
            SetResult (Android.App.Result.Canceled);

            // ReSharper disable once UseObjectOrCollectionInitializer
            _activityCommon = new ActivityCommon(this, () =>
            {
                if (_activityCommon.MtcBtServiceBound)
                {
                    UpdateMtcDevices();
                }
            },
            (context, intent) =>
            {
                if (intent != null && intent.Action == GlobalBroadcastReceiver.NotificationBroadcastAction)
                {
                    if (intent.HasExtra(GlobalBroadcastReceiver.BtScanFinished))
                    {
                        ShowScanState(false);
                    }
                }
            });
            _activityCommon.SelectedInterface = ActivityCommon.InterfaceType.Bluetooth;

            _appDataDir = Intent.GetStringExtra(ExtraAppDataDir);

            // Initialize the button to perform device discovery
            _scanButton = FindViewById<Button>(Resource.Id.button_scan);
            _scanButton.Click += (sender, e) =>
            {
                if (_activityCommon.MtcBtServiceBound)
                {
                    DoMtcDiscovery();
                }
                else
                {
                    DoDiscovery();
                }
            };

            // Initialize array adapters. One for already paired devices and
            // one for newly discovered devices
            _pairedDevicesArrayAdapter = new ArrayAdapter<string> (this, Resource.Layout.device_name);
            _newDevicesArrayAdapter = new ArrayAdapter<string> (this, Resource.Layout.device_name);

            // Find and set up the ListView for paired devices
            var pairedListView = FindViewById<ListView> (Resource.Id.paired_devices);
            pairedListView.Adapter = _pairedDevicesArrayAdapter;
            pairedListView.ItemClick += (sender, args) =>
            {
                DeviceListClick(sender, args, true);
            };

            // Find and set up the ListView for newly discovered devices
            var newDevicesListView = FindViewById<ListView> (Resource.Id.new_devices);
            newDevicesListView.Adapter = _newDevicesArrayAdapter;
            newDevicesListView.ItemClick += (sender, args) =>
            {
                DeviceListClick(sender, args, false);
            };

            // Register for broadcasts when a device is discovered
            _receiver = new Receiver (this);
            var filter = new IntentFilter (BluetoothDevice.ActionFound);
            RegisterReceiver (_receiver, filter);

            // Register for broadcasts when a device name changed
            filter = new IntentFilter(BluetoothDevice.ActionNameChanged);
            RegisterReceiver(_receiver, filter);

            // Register for broadcasts when discovery has finished
            filter = new IntentFilter (BluetoothAdapter.ActionDiscoveryFinished);
            RegisterReceiver (_receiver, filter);

            // register device changes
            filter = new IntentFilter();
            filter.AddAction(BluetoothDevice.ActionAclConnected);
            filter.AddAction(BluetoothDevice.ActionAclDisconnected);
            RegisterReceiver(_receiver, filter);

            // Get the local Bluetooth adapter
            _btAdapter = BluetoothAdapter.DefaultAdapter;

            // Get a set of currently paired devices
            if (!_activityCommon.MtcBtService)
            {
                UpdatePairedDevices();
            }
        }

        protected override void OnStart()
        {
            base.OnStart();
            if (_activityCommon.MtcBtService)
            {
                _activityCommon.StartMtcService();
            }
        }

        protected override void OnResume()
        {
            base.OnResume();
            UpdatePairedDevices();
        }

        protected override void OnStop()
        {
            base.OnStop();
            if (_activityCommon.MtcBtService)
            {
                MtcStopScan();
                _activityCommon.StopMtcService();
            }
        }

        protected override void OnDestroy ()
        {
            base.OnDestroy ();

            // Make sure we're not doing discovery anymore
            _btAdapter?.CancelDiscovery ();

            // Unregister broadcast listeners
            UnregisterReceiver (_receiver);
            _activityCommon.Dispose();
            _activityCommon = null;
        }

        public override bool OnOptionsItemSelected(IMenuItem item)
        {
            switch (item.ItemId)
            {
                case Android.Resource.Id.Home:
                    Finish();
                    return true;
            }
            return base.OnOptionsItemSelected(item);
        }

        private void UpdatePairedDevices()
        {
            // Get a set of currently paired devices
            var pairedDevices = _btAdapter.BondedDevices;

            // If there are paired devices, add each one to the ArrayAdapter
            _pairedDevicesArrayAdapter.Clear();
            if (pairedDevices.Count > 0)
            {
                foreach (var device in pairedDevices)
                {
                    if (device == null)
                    {
                        continue;
                    }
                    try
                    {
                        ParcelUuid[] uuids = device.GetUuids();
                        if ((uuids == null) || (uuids.Any(uuid => SppUuid.CompareTo(uuid.Uuid) == 0)))
                        {
                            _pairedDevicesArrayAdapter.Add(device.Name + "\n" + device.Address);
                        }
                    }
                    catch (Exception)
                    {
                        // ignored
                    }
                }
            }
            else
            {
                // ReSharper disable once ConvertIfStatementToConditionalTernaryExpression
                if (_btAdapter.IsEnabled)
                {
                    _pairedDevicesArrayAdapter.Add(Resources.GetText(Resource.String.none_paired));
                }
                else
                {
                    _pairedDevicesArrayAdapter.Add(Resources.GetText(Resource.String.bt_not_enabled));
                }
            }
        }

        private void UpdateMtcDevices()
        {
            _pairedDevicesArrayAdapter.Clear();
            _newDevicesArrayAdapter.Clear();
            if (!_activityCommon.MtcBtServiceBound)
            {
                return;
            }
            MtcServiceConnection mtcServiceConnection = _activityCommon.MtcServiceConnection;
            try
            {
                FindViewById<View>(Resource.Id.layout_new_devices).Visibility = ViewStates.Visible;

                int offset = mtcServiceConnection.ApiVersion < 2 ? 0 : 1;
                long nowDevAddr = mtcServiceConnection.GetNowDevAddr();
                string nowDevAddrString = string.Format(CultureInfo.InvariantCulture, "{0:X012}", nowDevAddr);
                IList<string> deviceList = mtcServiceConnection.GetDeviceList();
                IList<string> matchList = mtcServiceConnection.GetMatchList();
                foreach (string device in matchList)
                {
                    if (ExtractMtcDeviceInfo(offset, device, out string name, out string address))
                    {
                        string mac = address.Replace(":", string.Empty);
                        if (string.Compare(mac, nowDevAddrString, StringComparison.OrdinalIgnoreCase) == 0)
                        {
                            name += " " + GetString(Resource.String.bt_device_connected);
                        }
                        _pairedDevicesArrayAdapter.Add(name + "\n" + address);
                    }
                }
                foreach (string device in deviceList)
                {
                    if (ExtractMtcDeviceInfo(offset, device, out string name, out string address))
                    {
                        _newDevicesArrayAdapter.Add(name + "\n" + address);
                    }
                }
                if (_newDevicesArrayAdapter.Count == 0)
                {
                    _newDevicesArrayAdapter.Add(Resources.GetText(Resource.String.none_found));
                }
            }
            catch (Exception)
            {
                // ignored
            }
        }

        /// <summary>
        /// Extract device info for MTC devices
        /// </summary>
        /// <param name="offset">MAC offset: 0, 1</param>
        /// <param name="device">Complete device info text</param>
        /// <param name="name">Device name</param>
        /// <param name="address">Device address</param>
        /// <returns>True: Success</returns>
        private static bool ExtractMtcDeviceInfo(int offset, string device, out string name, out string address)
        {
            name = string.Empty;
            address = string.Empty;
            if (device.Length < offset + 12)
            {
                return false;
            }
            string mac = device.Substring(offset, 12);
            StringBuilder sb = new StringBuilder();
            address = string.Empty;
            for (int i = 0; i < 12; i += 2)
            {
                if (sb.Length > 0)
                {
                    sb.Append(":");
                }
                sb.Append(mac.Substring(i, 2));
            }
            address = sb.ToString();
            name = device.Substring(offset + 12);
            return true;
        }

        /// <summary>
        /// MTC stop BT scan
        /// </summary>
        /// <returns>True if successful</returns>
        // ReSharper disable once UnusedMethodReturnValue.Local
        private bool MtcStopScan()
        {
            if (_activityCommon.MtcBtServiceBound)
            {
                try
                {
                    _activityCommon.MtcServiceConnection.ScanStop();
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }
            return false;
        }

        /// <summary>
        /// Show scan state
        /// </summary>
        /// <param name="enabled">True if scanning is enabled</param>
        private void ShowScanState(bool enabled)
        {
            if (enabled)
            {
                FindViewById<ProgressBar>(Resource.Id.progress_bar).Visibility = ViewStates.Visible;
                SetTitle(Resource.String.scanning);
                _scanButton.Enabled = false;
            }
            else
            {
                FindViewById<ProgressBar>(Resource.Id.progress_bar).Visibility = ViewStates.Invisible;
                SetTitle(Resource.String.select_device);
                _scanButton.Enabled = true;
            }
        }

        /// <summary>
        /// Start device discover with the BluetoothAdapter
        /// </summary>
        private void DoDiscovery ()
        {
            // Log.Debug (Tag, "doDiscovery()");

            // If we're already discovering, stop it
            if (_btAdapter.IsDiscovering)
            {
                _btAdapter.CancelDiscovery ();
            }
            _newDevicesArrayAdapter.Clear();

            // Request discover from BluetoothAdapter
            if (_btAdapter.StartDiscovery())
            {
                // Indicate scanning in the title
                ShowScanState(true);

                // Turn on area for new devices
                FindViewById<View>(Resource.Id.layout_new_devices).Visibility = ViewStates.Visible;
            }
        }

        /// <summary>
        /// Start MTC device discovery
        /// </summary>
        private void DoMtcDiscovery()
        {
            // Log.Debug (Tag, "doDiscovery()");

            if (_activityCommon.MtcBtServiceBound)
            {
                try
                {
                    _activityCommon.MtcServiceConnection.ScanStart();
                    // Indicate scanning in the title
                    ShowScanState(true);

                    // Turn on area for new devices
                    FindViewById<View>(Resource.Id.layout_new_devices).Visibility = ViewStates.Visible;
                }
                catch (Exception)
                {
                    // ignored
                }
            }
        }

        /// <summary>
        /// Start adapter detection
        /// </summary>
        /// <param name="deviceAddress">Device Bluetooth address</param>
        /// <param name="deviceName">Device Bleutooth name</param>
        private void DetectAdapter(string deviceAddress, string deviceName)
        {
            CustomProgressDialog progress = new CustomProgressDialog(this);
            progress.SetMessage(GetString(Resource.String.detect_adapter));
            progress.ButtonAbort.Visibility = ViewStates.Gone;
            progress.Show();

            _sbLog.Clear();
            _deviceConnected = false;

            _activityCommon.ConnectMtcBtDevice(deviceAddress);

            Thread detectThread = new Thread(() =>
            {
                AdapterType adapterType = AdapterType.Unknown;
                try
                {
                    BluetoothDevice device = _btAdapter.GetRemoteDevice(deviceAddress);
                    if (device != null)
                    {
                        int connectTimeout = _activityCommon.MtcBtService ? 1000 : 2000;
                        _connectDeviceAddress = device.Address;
                        BluetoothSocket bluetoothSocket = null;

                        adapterType = AdapterType.ConnectionFailed;
                        if (adapterType == AdapterType.ConnectionFailed)
                        {
                            try
                            {
                                LogString("Connect with CreateRfcommSocketToServiceRecord");
                                bluetoothSocket = device.CreateRfcommSocketToServiceRecord(SppUuid);
                                if (bluetoothSocket != null)
                                {
                                    try
                                    {
                                        bluetoothSocket.Connect();
                                    }
                                    catch (Exception)
                                    {
                                        // sometimes the second connect is working
                                        bluetoothSocket.Connect();
                                    }
                                    _connectedEvent.WaitOne(connectTimeout, false);
                                    LogString(_deviceConnected ? "Bt device is connected" : "Bt device is not connected");
                                    adapterType = AdapterTypeDetection(bluetoothSocket);
                                    if (_activityCommon.MtcBtService && adapterType == AdapterType.Unknown)
                                    {
                                        for (int retry = 0; retry < 20; retry++)
                                        {
                                            LogString("Retry connect");
                                            bluetoothSocket.Close();
                                            bluetoothSocket.Connect();
                                            adapterType = AdapterTypeDetection(bluetoothSocket);
                                            if (adapterType != AdapterType.Unknown &&
                                                adapterType != AdapterType.ConnectionFailed)
                                            {
                                                break;
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                LogString("*** Connect exception: " + ex.Message);
                                adapterType = AdapterType.ConnectionFailed;
                            }
                            finally
                            {
                                bluetoothSocket?.Close();
                            }
                        }

                        if (adapterType == AdapterType.ConnectionFailed && !_activityCommon.MtcBtService)
                        {
                            try
                            {
                                LogString("Connect with createRfcommSocket");
                                // this socket sometimes looses data for long telegrams
                                IntPtr createRfcommSocket = Android.Runtime.JNIEnv.GetMethodID(device.Class.Handle,
                                    "createRfcommSocket", "(I)Landroid/bluetooth/BluetoothSocket;");
                                if (createRfcommSocket == IntPtr.Zero)
                                {
                                    throw new Exception("No createRfcommSocket");
                                }
                                IntPtr rfCommSocket = Android.Runtime.JNIEnv.CallObjectMethod(device.Handle,
                                    createRfcommSocket, new Android.Runtime.JValue(1));
                                if (rfCommSocket == IntPtr.Zero)
                                {
                                    throw new Exception("No rfCommSocket");
                                }
                                bluetoothSocket = GetObject<BluetoothSocket>(rfCommSocket,
                                    Android.Runtime.JniHandleOwnership.TransferLocalRef);
                                if (bluetoothSocket != null)
                                {
                                    bluetoothSocket.Connect();
                                    _connectedEvent.WaitOne(connectTimeout, false);
                                    LogString(_deviceConnected ? "Bt device is connected" : "Bt device is not connected");
                                    adapterType = AdapterTypeDetection(bluetoothSocket);
                                }
                            }
                            catch (Exception ex)
                            {
                                LogString("*** Connect exception: " + ex.Message);
                                adapterType = AdapterType.ConnectionFailed;
                            }
                            finally
                            {
                                bluetoothSocket?.Close();
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    adapterType = AdapterType.ConnectionFailed;
                }

                RunOnUiThread(() =>
                {
                    if (_activityCommon == null)
                    {
                        return;
                    }
                    progress.Dismiss();
                    progress.Dispose();

                    switch (adapterType)
                    {
                        case AdapterType.ConnectionFailed:
                        {
                            if (_activityCommon.MtcBtService)
                            {
                                _altertInfoDialog = new AlertDialog.Builder(this)
                                    .SetNeutralButton(Resource.String.button_ok, (sender, args) => { })
                                    .SetCancelable(true)
                                    .SetMessage(Resource.String.adapter_connection_mtc_failed)
                                    .SetTitle(Resource.String.alert_title_error)
                                    .Show();
                                _altertInfoDialog.DismissEvent += (sender, args) =>
                                {
                                    _altertInfoDialog = null;
                                };
                                break;
                            }

                            _altertInfoDialog = new AlertDialog.Builder(this)
                                .SetPositiveButton(Resource.String.button_yes, (sender, args) =>
                                {
                                    ReturnDeviceType(deviceAddress + ";" + EdBluetoothInterface.RawTag, deviceName);
                                })
                                .SetNegativeButton(Resource.String.button_no, (sender, args) =>
                                {
                                })
                                .SetCancelable(true)
                                .SetMessage(Resource.String.adapter_connection_failed)
                                .SetTitle(Resource.String.alert_title_error)
                                .Show();
                            _altertInfoDialog.DismissEvent += (sender, args) =>
                            {
                                _altertInfoDialog = null;
                            };
                            break;
                        }

                        case AdapterType.Unknown:
                        {
                            if (_activityCommon.MtcBtService)
                            {
                                _altertInfoDialog = new AlertDialog.Builder(this)
                                    .SetNeutralButton(Resource.String.button_ok, (sender, args) => { })
                                    .SetCancelable(true)
                                    .SetMessage(Resource.String.adapter_connection_mtc_failed)
                                    .SetTitle(Resource.String.alert_title_error)
                                    .Show();
                                _altertInfoDialog.DismissEvent += (sender, args) =>
                                {
                                    if (_activityCommon == null)
                                    {
                                        return;
                                    }
                                    _altertInfoDialog = null;
                                    _activityCommon.RequestSendMessage(_appDataDir, _sbLog.ToString(),
                                        PackageManager.GetPackageInfo(PackageName, 0), GetType(), (o, eventArgs) => { });
                                };
                                break;
                            }

                            bool yesSelected = false;
                            _altertInfoDialog = new AlertDialog.Builder(this)
                                .SetPositiveButton(Resource.String.button_yes, (sender, args) =>
                                {
                                    yesSelected = true;
                                })
                                .SetNegativeButton(Resource.String.button_no, (sender, args) =>
                                {
                                })
                                .SetCancelable(true)
                                .SetMessage(Resource.String.unknown_adapter_type)
                                .SetTitle(Resource.String.alert_title_error)
                                .Show();
                            _altertInfoDialog.DismissEvent += (sender, args) =>
                            {
                                if (_activityCommon == null)
                                {
                                    return;
                                }
                                _altertInfoDialog = null;
                                _activityCommon.RequestSendMessage(_appDataDir, _sbLog.ToString(),
                                    PackageManager.GetPackageInfo(PackageName, 0), GetType(), (o, eventArgs) =>
                                    {
                                        if (yesSelected)
                                        {
                                            ReturnDeviceType(deviceAddress + ";" + EdBluetoothInterface.RawTag, deviceName);
                                        }
                                    });
                            };
                            break;
                        }

                        case AdapterType.Elm327:
                        {
                            _altertInfoDialog = new AlertDialog.Builder(this)
                                .SetNeutralButton(Resource.String.button_ok, (sender, args) =>
                                {
                                    ReturnDeviceType(deviceAddress + ";" + EdBluetoothInterface.Elm327Tag, deviceName);
                                })
                                .SetCancelable(true)
                                .SetMessage(Resource.String.adapter_elm_replacement)
                                .SetTitle(Resource.String.alert_title_info)
                                .Show();
                            _altertInfoDialog.DismissEvent += (sender, args) =>
                            {
                                _altertInfoDialog = null;
                            };
                            TextView messageView = _altertInfoDialog.FindViewById<TextView>(Android.Resource.Id.Message);
                            if (messageView != null)
                            {
                                messageView.MovementMethod = new LinkMovementMethod();
                            }
                            break;
                        }

                        case AdapterType.Elm327Invalid:
                        case AdapterType.Elm327Fake21:
                        {
                            string message =
                                GetString(adapterType == AdapterType.Elm327Fake21
                                    ? Resource.String.fake_elm_adapter_type
                                    : Resource.String.invalid_adapter_type);
                            message += "<br>" + GetString(Resource.String.recommened_adapter_type);
                            _altertInfoDialog = new AlertDialog.Builder(this)
                                .SetNeutralButton(Resource.String.button_ok, (sender, args) =>
                                {
                                })
                                .SetCancelable(true)
                                .SetMessage(ActivityCommon.FromHtml(message))
                                .SetTitle(Resource.String.alert_title_error)
                                .Show();
                            _altertInfoDialog.DismissEvent += (sender, args) =>
                            {
                                if (_activityCommon == null)
                                {
                                    return;
                                }
                                _altertInfoDialog = null;
                                _activityCommon.RequestSendMessage(_appDataDir, _sbLog.ToString(), PackageManager.GetPackageInfo(PackageName, 0), GetType());
                            };
                            TextView messageView = _altertInfoDialog.FindViewById<TextView>(Android.Resource.Id.Message);
                            if (messageView != null)
                            {
                                messageView.MovementMethod = new LinkMovementMethod();
                            }
                            break;
                        }

                        case AdapterType.Custom:
                        case AdapterType.CustomUpdate:
                            _altertInfoDialog = new AlertDialog.Builder(this)
                                .SetPositiveButton(Resource.String.button_yes, (sender, args) =>
                                {
                                    ReturnDeviceType(deviceAddress, deviceName, true);
                                })
                                .SetNegativeButton(Resource.String.button_no, (sender, args) =>
                                {
                                    ReturnDeviceType(deviceAddress, deviceName);
                                })
                                .SetCancelable(true)
                                .SetMessage(adapterType == AdapterType.CustomUpdate ? Resource.String.adapter_fw_update : Resource.String.adapter_cfg_required)
                                .SetTitle(Resource.String.alert_title_info)
                                .Show();
                            _altertInfoDialog.DismissEvent += (sender, args) =>
                            {
                                _altertInfoDialog = null;
                            };
                            break;

                        case AdapterType.EchoOnly:
                            ReturnDeviceType(deviceAddress + ";" + EdBluetoothInterface.RawTag, deviceName);
                            break;

                        default:
                            ReturnDeviceType(deviceAddress, deviceName);
                            break;
                    }
                });
            })
            {
                Priority = System.Threading.ThreadPriority.Highest
            };
            detectThread.Start();
        }

        /// <summary>
        /// Return specified device type to caller
        /// </summary>
        /// <param name="deviceAddress">Device Bluetooth address</param>
        /// <param name="deviceName">Device Bleutooth name</param>
        /// <param name="adapterConfig">Opend adapter configuration</param>
        private void ReturnDeviceType(string deviceAddress, string deviceName, bool adapterConfig = false)
        {
            // Create the result Intent and include the MAC address
            Intent intent = new Intent();
            intent.PutExtra(ExtraDeviceName, deviceName);
            intent.PutExtra(ExtraDeviceAddress, deviceAddress);
            intent.PutExtra(ExtraCallAdapterConfig, adapterConfig);

            // Set result and finish this Activity
            SetResult(Android.App.Result.Ok, intent);
            Finish();
        }

        /// <summary>
        /// Detects the CAN adapter type
        /// </summary>
        /// <param name="bluetoothSocket">Bluetooth socket for communication</param>
        /// <returns>Adapter type</returns>
        private AdapterType AdapterTypeDetection(BluetoothSocket bluetoothSocket)
        {
            const int versionRespLen = 9;
            byte[] customData = { 0x82, 0xF1, 0xF1, 0xFD, 0xFD, 0x5E };
            AdapterType adapterType = AdapterType.Unknown;

            try
            {
                Stream bluetoothInStream = bluetoothSocket.InputStream;
                Stream bluetoothOutStream = bluetoothSocket.OutputStream;

                // custom adapter
                bluetoothInStream.Flush();
                while (bluetoothInStream.IsDataAvailable())
                {
                    bluetoothInStream.ReadByte();
                }
                LogData(customData, 0, customData.Length, "Send");
                bluetoothOutStream.Write(customData, 0, customData.Length);

                LogData(null, 0, 0, "Resp");
                List<byte> responseList = new List<byte>();
                long startTime = Stopwatch.GetTimestamp();
                for (; ; )
                {
                    while (bluetoothInStream.IsDataAvailable())
                    {
                        int data = bluetoothInStream.ReadByte();
                        if (data >= 0)
                        {
                            LogByte((byte)data);
                            responseList.Add((byte)data);
                            startTime = Stopwatch.GetTimestamp();
                        }
                    }
                    if (responseList.Count >= customData.Length + versionRespLen)
                    {
                        LogString("Custom adapter length");
                        bool validEcho = !customData.Where((t, i) => responseList[i] != t).Any();
                        if (!validEcho)
                        {
                            LogString("*** Echo incorrect");
                            break;
                        }
                        byte checkSum = 0x00;
                        for (int i = 0; i < versionRespLen - 1; i++)
                        {
                            checkSum += responseList[i + customData.Length];
                        }
                        if (checkSum != responseList[customData.Length + versionRespLen - 1])
                        {
                            LogString("*** Checksum incorrect");
                            break;
                        }
                        int adapterTypeId = responseList[customData.Length + 5] + (responseList[customData.Length + 4] << 8);
                        int fwVersion = responseList[customData.Length + 7] + (responseList[customData.Length + 6] << 8);
                        int fwUpdateVersion = PicBootloader.GetFirmwareVersion((uint)adapterTypeId);
                        if (fwUpdateVersion >= 0 && fwUpdateVersion > fwVersion)
                        {
                            LogString("Custom adapter with old firmware detected");
                            return AdapterType.CustomUpdate;
                        }
                        LogString("Custom adapter detected");
                        return AdapterType.Custom;
                    }
                    if (Stopwatch.GetTimestamp() - startTime > ResponseTimeout * TickResolMs)
                    {
                        if (responseList.Count >= customData.Length)
                        {
                            bool validEcho = !customData.Where((t, i) => responseList[i] != t).Any();
                            if (validEcho)
                            {
                                LogString("Valid echo detected");
                                adapterType = AdapterType.EchoOnly;
                            }
                        }
                        break;
                    }
                }
                LogString("No custom adapter found");

                // ELM327
                bool elmReports21 = false;
                for (int retries = 0; retries < 2; retries++)
                {
                    bluetoothInStream.Flush();
                    while (bluetoothInStream.IsDataAvailable())
                    {
                        bluetoothInStream.ReadByte();
                    }
                    byte[] sendData = Encoding.UTF8.GetBytes("ATI\r");
                    LogData(sendData, 0, sendData.Length, "Send");
                    bluetoothOutStream.Write(sendData, 0, sendData.Length);

                    string response = GetElm327Reponse(bluetoothInStream);
                    if (response != null)
                    {
                        if (response.Contains("ELM327"))
                        {
                            LogString("ELM327 detected");
                            if (response.Contains("ELM327 v2.1"))
                            {
                                LogString("Version 2.1 detected");
                                elmReports21 = true;
                            }
                            adapterType = AdapterType.Elm327;
                            break;
                        }
                    }
                }
                if (adapterType == AdapterType.Elm327)
                {
                    foreach (string command in EdBluetoothInterface.Elm327InitCommands)
                    {
                        bluetoothInStream.Flush();
                        while (bluetoothInStream.IsDataAvailable())
                        {
                            bluetoothInStream.ReadByte();
                        }
                        byte[] sendData = Encoding.UTF8.GetBytes(command + "\r");
                        LogData(sendData, 0, sendData.Length, "Send");
                        bluetoothOutStream.Write(sendData, 0, sendData.Length);

                        string response = GetElm327Reponse(bluetoothInStream);
                        if (response == null)
                        {
                            LogString("*** No ELM response");
                            adapterType = AdapterType.Elm327Invalid;
                            break;
                        }
                        if (!response.Contains("OK\r"))
                        {
                            LogString("*** No ELM OK found");
                            adapterType = AdapterType.Elm327Invalid;
                            break;
                        }
                    }
                    if (adapterType == AdapterType.Elm327Invalid && elmReports21)
                    {
                        adapterType = AdapterType.Elm327Fake21;
                    }
                }
            }
            catch (Exception ex)
            {
                LogString("*** Exception: " + ex.Message);
                return AdapterType.ConnectionFailed;
            }
            LogString("Adapter type: " + adapterType);
            return adapterType;
        }

        /// <summary>
        /// Get response from EL327
        /// </summary>
        /// <param name="bluetoothInStream">Bluetooth input stream</param>
        /// <returns>Response string, null for no reponse</returns>
        private string GetElm327Reponse(Stream bluetoothInStream)
        {
            LogData(null, 0, 0, "Resp");
            string response = null;
            StringBuilder stringBuilder = new StringBuilder();
            long startTime = Stopwatch.GetTimestamp();
            for (; ; )
            {
                while (bluetoothInStream.IsDataAvailable())
                {
                    int data = bluetoothInStream.ReadByte();
                    if (data >= 0 && data != 0x00)
                    {
                        // remove 0x00
                        LogByte((byte)data);
                        stringBuilder.Append(Convert.ToChar(data));
                        startTime = Stopwatch.GetTimestamp();
                    }
                    if (data == 0x3E)
                    {
                        // prompt
                        response = stringBuilder.ToString();
                        break;
                    }
                    if (stringBuilder.Length > 100)
                    {
                        LogString("*** ELM response too long");
                        break;
                    }
                }
                if (response != null)
                {
                    break;
                }
                if (Stopwatch.GetTimestamp() - startTime > ResponseTimeout * TickResolMs)
                {
                    LogString("*** ELM response timeout");
                    break;
                }
            }
            if (response == null)
            {
                LogString("*** No ELM prompt");
            }
            return response;
        }

        /// <summary>
        /// The on-click listener for all devices in the ListViews
        /// </summary>
        // ReSharper disable once UnusedParameter.Local
        private void DeviceListClick(object sender, AdapterView.ItemClickEventArgs e, bool paired)
        {
            if (_activityCommon.MtcBtServiceBound)
            {
                MtcStopScan();
            }
            else
            {
                // Cancel discovery because it's costly and we're about to connect
                if (_btAdapter.IsDiscovering)
                {
                    _btAdapter.CancelDiscovery();
                }
            }
            ShowScanState(false);

            if (e.View is TextView textView)
            {
                string info = textView.Text;
                if (!ExtractDeviceInfo(info, out string name, out string address))
                {
                    return;
                }

                if (_activityCommon.MtcBtServiceBound)
                {
                    SelectMtcDeviceAction(name, address, paired);
                }
                else
                {
                    DetectAdapter(address, name);
                }
            }
        }

        /// <summary>
        /// Select action for device in MTC mode
        /// </summary>
        private void SelectMtcDeviceAction(string name, string address, bool paired)
        {
            if (!_activityCommon.MtcBtServiceBound)
            {
                return;
            }
            string mac = address.Replace(":", string.Empty);
            long nowDevAddr = _activityCommon.MtcServiceConnection.GetNowDevAddr();
            string nowDevAddrString = string.Format(CultureInfo.InvariantCulture, "{0:X012}", nowDevAddr);
            bool connectedPhone = string.Compare(nowDevAddrString, mac, StringComparison.OrdinalIgnoreCase) == 0;

            List<BtOperation> operationList = new List<BtOperation>();
            List<string> itemList = new List<string>();
            if (paired && !connectedPhone)
            {
                itemList.Add(GetString(Resource.String.bt_device_select));
                operationList.Add(BtOperation.SelectAdapter);
            }
            if (!paired)
            {
                itemList.Add(GetString(Resource.String.bt_device_connect_obd));
                operationList.Add(BtOperation.ConnectObd);
            }
            if (!connectedPhone)
            {
                itemList.Add(GetString(Resource.String.bt_device_connect_phone));
                operationList.Add(BtOperation.ConnectPhone);
            }
            if (paired && connectedPhone)
            {
                itemList.Add(GetString(Resource.String.bt_device_disconnect_phone));
                operationList.Add(BtOperation.DisconnectPhone);
            }
            itemList.Add(GetString(Resource.String.bt_device_delete));
            operationList.Add(BtOperation.DeleteDevice);

            Java.Lang.ICharSequence[] items = new Java.Lang.ICharSequence[itemList.Count];
            for (int i = 0; i < itemList.Count; i++)
            {
                items[i] = new Java.Lang.String(itemList[i]);
            }

            AlertDialog.Builder builder = new AlertDialog.Builder(this);
            builder.SetTitle(Resource.String.bt_device_menu_tite);
            builder.SetItems(items, (sender, args) =>
                {
                    if (!_activityCommon.MtcBtServiceBound)
                    {
                        return;
                    }
                    if (args.Which < 0 || args.Which >= operationList.Count)
                    {
                        return;
                    }
                    try
                    {
                        switch (operationList[args.Which])
                        {
                            case BtOperation.SelectAdapter:
                                if (!_activityCommon.MtcBtConnected)
                                {
                                    new AlertDialog.Builder(this)
                                        .SetMessage(Resource.String.mtc_disconnect_warn)
                                        .SetTitle(Resource.String.alert_title_warning)
                                        .SetPositiveButton(Resource.String.button_yes, (s, e) =>
                                        {
                                            DetectAdapter(address, name);
                                        })
                                        .SetNegativeButton(Resource.String.button_no, (s, e) =>
                                        {
                                        })
                                        .Show();
                                    break;
                                }
                                DetectAdapter(address, name);
                                break;

                            case BtOperation.ConnectObd:
                                _activityCommon.MtcServiceConnection.ConnectObd(mac);
                                break;

                            case BtOperation.ConnectPhone:
                                _activityCommon.MtcServiceConnection.ConnectBt(mac);
                                break;

                            case BtOperation.DisconnectPhone:
                                _activityCommon.MtcServiceConnection.DisconnectBt(mac);
                                break;

                            case BtOperation.DeleteDevice:
                                _activityCommon.MtcServiceConnection.DeleteBt(mac);
                                break;
                        }
                    }
                    catch (Exception)
                    {
                        // ignored
                    }
                });
            builder.Show();
        }

        /// <summary>
        /// Extract device info from name
        /// </summary>
        /// <param name="info">Complete device info text</param>
        /// <param name="name">Device name</param>
        /// <param name="address">Device address</param>
        private static bool ExtractDeviceInfo(string info, out string name, out string address)
        {
            string[] parts = info.Split('\n');
            if (parts.Length < 2)
            {
                name = string.Empty;
                address = string.Empty;
                return false;
            }
            name = parts[0];
            address = parts[1];
            return true;
        }

        private void LogData(byte[] data, int offset, int length, string info = null)
        {
            if (!string.IsNullOrEmpty(info))
            {
                if (_sbLog.Length > 0)
                {
                    _sbLog.Append("\n");
                }
                _sbLog.Append(" (");
                _sbLog.Append(info);
                _sbLog.Append("): ");
            }
            if (data != null)
            {
                for (int i = 0; i < length; i++)
                {
                    _sbLog.Append(string.Format(ActivityMain.Culture, "{0:X02} ", data[offset + i]));
                }
            }
        }

        private void LogString(string info)
        {
            if (_sbLog.Length > 0)
            {
                _sbLog.Append("\n");
            }
            _sbLog.Append(info);
        }

        private void LogByte(byte data)
        {
            _sbLog.Append(string.Format(ActivityMain.Culture, "{0:X02} ", data));
        }

        public class Receiver : BroadcastReceiver
        {
            readonly DeviceListActivity _chat;

            public Receiver(DeviceListActivity chat)
            {
                _chat = chat;
            }

            public override void OnReceive (Context context, Intent intent)
            {
                try
                {
                    string action = intent.Action;

                    switch (action)
                    {
                        case BluetoothDevice.ActionFound:
                        case BluetoothDevice.ActionNameChanged:
                        {
                            // Get the BluetoothDevice object from the Intent
                            BluetoothDevice device = (BluetoothDevice) intent.GetParcelableExtra(BluetoothDevice.ExtraDevice);
                            // If it's already paired, skip it, because it's been listed already
                            if (device.BondState != Bond.Bonded)
                            {
                                ParcelUuid[] uuids = device.GetUuids();
                                if ((uuids == null) || (uuids.Any(uuid => SppUuid.CompareTo(uuid.Uuid) == 0)))
                                {
                                    // check for multiple entries
                                    int index = -1;
                                    for (int i = 0; i < _newDevicesArrayAdapter.Count; i++)
                                    {
                                        string item = _newDevicesArrayAdapter.GetItem(i);
                                        if (!ExtractDeviceInfo(_newDevicesArrayAdapter.GetItem(i), out string _, out string address))
                                        {
                                            return;
                                        }
                                        if (string.Compare(address, device.Address, StringComparison.OrdinalIgnoreCase) == 0)
                                        {
                                            _newDevicesArrayAdapter.Remove(item);
                                            index = i;
                                            break;
                                        }
                                    }
                                    string newName = device.Name + "\n" + device.Address;
                                    if (index < 0)
                                    {
                                        _newDevicesArrayAdapter.Add(newName);
                                    }
                                    else
                                    {
                                        _newDevicesArrayAdapter.Insert(newName, index);
                                    }
                                }
                            }
                            break;
                        }

                        case BluetoothAdapter.ActionDiscoveryFinished:
                            // When discovery is finished, change the Activity title
                            _chat.ShowScanState(false);
                            if (_newDevicesArrayAdapter.Count == 0)
                            {
                                _newDevicesArrayAdapter.Add(_chat.Resources.GetText(Resource.String.none_found));
                            }
                            break;

                        case BluetoothDevice.ActionAclConnected:
                        case BluetoothDevice.ActionAclDisconnected:
                        {
                            BluetoothDevice device = (BluetoothDevice)intent.GetParcelableExtra(BluetoothDevice.ExtraDevice);
                            if (device != null)
                            {
                                if (!string.IsNullOrEmpty(_chat._connectDeviceAddress) &&
                                        string.Compare(device.Address, _chat._connectDeviceAddress, StringComparison.OrdinalIgnoreCase) == 0)
                                {
                                    _chat._deviceConnected = action == BluetoothDevice.ActionAclConnected;
                                    _chat._connectedEvent.Set();
                                }
                            }
                            break;
                        }
                    }
                }
                catch (Exception)
                {
                    // ignored
                }
            }
        }
    }

    public class SelectListener : Java.Lang.Object, IDialogInterfaceOnClickListener
    {
        private readonly Context _context;

        public SelectListener(Context context)
        {
            _context = context;
        }

        public void OnClick(IDialogInterface dialog, Int32 which)
        {
            switch (which)
            {
                case 1:
                    Toast.MakeText(_context, "Button1", ToastLength.Long);
                    break;

                case 2:
                    Toast.MakeText(_context, "Button2", ToastLength.Long);
                    break;

                case 3:
                    Toast.MakeText(_context, "Button3", ToastLength.Long);
                    break;

                case 4:
                    Toast.MakeText(_context, "Button4", ToastLength.Long);
                    break;
            }
        }
    }
}

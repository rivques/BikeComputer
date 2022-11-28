using Xamarin.Forms;
using Xamarin.Forms.Xaml;

using Xamarin.Essentials;
using System;
using System.Threading.Tasks;
using System.Threading;
using Plugin.BLE;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.Exceptions;
using System.Text;
using System.Numerics;
using System.Collections.Generic;
using Plugin.BLE.Abstractions;
using System.Linq.Expressions;
using System.IO;

namespace BikeComputer.Views
{
    [XamlCompilation(XamlCompilationOptions.Compile)]
    public partial class MainPage : ContentPage
    {
        public MainPage()
        {
            InitializeComponent();
        }

        List<DateTime> ticks = new List<DateTime>();
        double currentSpeed = 0;

        bool shouldStopTimer = false;
        SensorSpeed speed = SensorSpeed.UI;
        AccelerometerData? currentAccel = null;
        double currentHeading = 0;
        Location currentLocation = null;

        CancellationTokenSource btCts;
        IDevice connectedDevice = null;
        //List<IDevice> deviceList = new List<IDevice>();

        string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string trailPath = null;

        bool isTrailblazing = false;

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            DeviceDisplay.KeepScreenOn= true;
            // bluetooth handler
            CrossBluetoothLE.Current.Adapter.DeviceDisconnected += Adapter_DeviceDisconnected;

            // accel and compass
            Accelerometer.ReadingChanged += Accelerometer_ReadingChanged;
            Accelerometer.Start(speed);
            Compass.ReadingChanged += Compass_ReadingChanged;
            Compass.Start(speed);

            // GPS
            shouldStopTimer = false;
            Device.StartTimer(TimeSpan.FromSeconds(1), () =>
            {
                Task.Run(async () =>
                {
                    var result = await GetCurrentLocation();
                    Device.BeginInvokeOnMainThread(() =>
                    {
                        LocationLabel.Text = result;
                    });
                });
                return !shouldStopTimer;
            });

            // speed updating
            Device.StartTimer(TimeSpan.FromSeconds(.5), () =>
            {
                // compute speed
                // cull old ticks
                try
                {
                    while (DateTime.Now - ticks[0] > TimeSpan.FromMilliseconds(4000))
                    {
                        ticks.RemoveAt(0);
                    }
                }
                catch (ArgumentOutOfRangeException e)
                {
                    // this is fine, the array is just empty
                }
                // speed = distance/time, distance = 24*PI*number of ticks in the last second; time is 2 seconds
                double inchesPerSecond = 24 * 3.14159 * ticks.Count / 4;
                currentSpeed = inchesPerSecond * 0.05682; // mph
                Device.BeginInvokeOnMainThread(() =>
                {
                    SpeedLabel.Text = Math.Round(currentSpeed).ToString();
                });
                ;
                return !shouldStopTimer;
            });
        }

        private void Adapter_DeviceDisconnected(object sender, Plugin.BLE.Abstractions.EventArgs.DeviceEventArgs e)
        {
            connectedDevice = null;
            bluetoothButton.Text = "BT Options (Disconnected)";
        }

        public async void OnBTButtonClicked(object caller, EventArgs e)
        {
            string action = await DisplayActionSheet("Bluetooth Controls", "Cancel", "Disconnect", "Connect", "Stop Connecting");
            switch (action)
            {
                case "Disconnect":
                    if(connectedDevice != null)
                    {
                        await CrossBluetoothLE.Current.Adapter.DisconnectDeviceAsync(connectedDevice);
                        connectedDevice = null;
                    }
                    bluetoothButton.Text = "BT Options (Disconnected)";
                    break;
                case "Connect":
                    bluetoothButton.Text = "BT Options (Connecting)";
                    await connectToESP32();
                    break;
                case "Stop Connecting":
                    if (btCts != null && !btCts.IsCancellationRequested)
                        btCts.Cancel();
                    break;
                default: break;
            }
        }

        private async Task connectToESP32() { 
            var ble = CrossBluetoothLE.Current;
            var adapter = ble.Adapter;

            btCts = new CancellationTokenSource();
            connectedDevice = null;
            ConnectParameters parameters = new ConnectParameters();
            try
            {
                connectedDevice = await adapter.ConnectToKnownDeviceAsync(Guid.Parse("f971f253-ae40-3ea7-f5e0-336be023f148"), parameters, cancellationToken: btCts.Token);
            }
            catch (DeviceConnectionException e)
            {
                // ... could not connect to device
                Console.WriteLine(e.Message);
                return;
            }
            catch (TaskCanceledException)
            {
                Console.WriteLine("BT connecting cancelled");
                Device.BeginInvokeOnMainThread(() =>
                {
                    bluetoothButton.Text = "BT Options (Disconnected)";
                });
                return;
            }
            Console.WriteLine("We appear to have connected");
            foreach (var aService in await connectedDevice.GetServicesAsync())
            {
                Console.WriteLine($"Name: {aService.Name}, ID: {aService.Id.ToString()}");
            }
            var service = await connectedDevice.GetServiceAsync(Guid.Parse("6E400001-B5A3-F393-E0A9-E50E24DCCA9E"));
            Console.WriteLine($"Got a service, name: {service.Name}");
            ICharacteristic writeCharacteristic = null;
            ICharacteristic notifyCharacteristic = null;
            foreach (var characteristic in await service.GetCharacteristicsAsync())
            {
                Console.WriteLine($"Name: {characteristic.Name}, ID: {characteristic.Id.ToString()}, readable: {characteristic.CanRead}, writeable: {characteristic.CanWrite}, updateable: {characteristic.CanUpdate}");
                if (characteristic.CanWrite)
                {
                    writeCharacteristic = characteristic;
                }
                if (characteristic.CanUpdate)
                {
                    notifyCharacteristic = characteristic;
                }
            }
            Console.WriteLine($"Got a write characteristic, name: {writeCharacteristic.Name}, readable: {writeCharacteristic.CanRead}, writeable: {writeCharacteristic.CanWrite}, updateable: {writeCharacteristic.CanUpdate}");
            await writeCharacteristic.WriteAsync(Encoding.UTF8.GetBytes("Hello Android!"));
            notifyCharacteristic.ValueUpdated += (o, args) =>
            {
                Console.WriteLine($"Got tick!");
                ticks.Add(DateTime.Now);
            };

            await notifyCharacteristic.StartUpdatesAsync();
            bluetoothButton.Text = "BT Options (Connected)";
        }

        public async void OnTrailblazingClicked(object sender, EventArgs e)
        {
            if (isTrailblazing)
            {
                isTrailblazing = false;
                (sender as Button).Text = "Start Trailblazing";
                File.AppendAllText(trailPath, @"
    </trkseg>
  </trk>
</gpx>");
            } else
            {
                string trailName = await DisplayPromptAsync("Name Trail", "What is this trail's name?");
                if(trailName == null || trailName == "")
                    return; // user cancelled
                trailPath = Path.Combine(documentsPath, trailName + ".gpx");
                File.AppendAllText(trailPath, $@"<gpx xmlns=""http://www.topografix.com/GPX/1/1"" xmlns:gpxx=""http://www.garmin.com/xmlschemas/GpxExtensions/v3"" xmlns:gpxtpx=""http://www.garmin.com/xmlschemas/TrackPointExtension/v1"" creator=""Oregon 400t"" version=""1.1"" xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xsi:schemaLocation=""http://www.topografix.com/GPX/1/1 http://www.topografix.com/GPX/1/1/gpx.xsd http://www.garmin.com/xmlschemas/GpxExtensions/v3 http://www.garmin.com/xmlschemas/GpxExtensionsv3.xsd http://www.garmin.com/xmlschemas/TrackPointExtension/v1 http://www.garmin.com/xmlschemas/TrackPointExtensionv1.xsd"">
<metadata>
    <link href=""http://www.garmin.com"">
      <text>Garmin International</text>
    </link>
    <time>{DateTime.Now.ToUniversalTime().ToString("s")}</time>
  </metadata>
  <trk>
    <name>{trailName}</name>
    <trkseg>");
                Device.StartTimer(TimeSpan.FromSeconds(5), makeNewTrailPoint);
                (sender as Button).Text = "Stop Trailblazing";
                isTrailblazing = true;
            }
        }

        private bool makeNewTrailPoint()
        {
            if (!isTrailblazing)
                return false; // don't add anotnher point, user requested stop 
            string trailPoint = $@"
      <trkpt lat=""{currentLocation.Latitude}"" lon=""{currentLocation.Longitude}"">
        <ele>{currentLocation.Altitude}</ele>
        <time>{DateTime.Now.ToUniversalTime().ToString("s")}</time>
        <magvar>{currentHeading}</magvar>
      </trkpt>";
            File.AppendAllText(trailPath, trailPoint);
            Console.WriteLine($"Made trail point: {trailPoint}");
            return true;
        }

        void Accelerometer_ReadingChanged(object sender, AccelerometerChangedEventArgs e)
        {
            var data = e.Reading;
            //Console.WriteLine($"Reading: X: {data.Acceleration.X}, Y: {data.Acceleration.Y}, Z: {data.Acceleration.Z}");
            Device.BeginInvokeOnMainThread(() =>
            {
                AccelLabel.Text = $"X: {Math.Round(data.Acceleration.X, 3):N3}, Y: {Math.Round(data.Acceleration.Y, 3):N3}, Z: {Math.Round(data.Acceleration.Z, 3):N3}";
            });
        }

        public void ToggleAccelerometer()
        {
            try
            {
                if (Accelerometer.IsMonitoring)
                    Accelerometer.Stop();
                else
                    Accelerometer.Start(speed);
            }
            catch (FeatureNotSupportedException fnsEx)
            {
                // Feature not supported on device
            }
            catch (Exception ex)
            {
                // Other error has occurred.
            }
        }

        void Compass_ReadingChanged(object sender, CompassChangedEventArgs e)
        {
            var data = e.Reading;
            currentHeading = data.HeadingMagneticNorth;
            //Console.WriteLine($"Reading: {currentHeading} degrees");
            Device.BeginInvokeOnMainThread(() =>
            {
                HeadingLabel.Text = Math.Round(currentHeading, 0).ToString() + "°";
            });
        }

        public void ToggleCompass()
        {
            try
            {
                if (Compass.IsMonitoring)
                    Compass.Stop();
                else
                    Compass.Start(speed);
            }
            catch (FeatureNotSupportedException fnsEx)
            {
                // Feature not supported on device
            }
            catch (Exception ex)
            {
                // Some other exception has occurred
            }
        }

        CancellationTokenSource gpsCts;

        async Task<String> GetCurrentLocation()
        {
            try
            {
                var request = new GeolocationRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(10));
                gpsCts = new CancellationTokenSource();
                var location = await Geolocation.GetLocationAsync(request, gpsCts.Token);

                if (location != null)
                {
                    //Console.WriteLine(location.ToString());
                    currentLocation = location;
                    return $"Lat: {Math.Round(location.Latitude, 6)}, Long: {Math.Round(location.Longitude, 6)}, Alt: {Math.Round((double)location.Altitude * 3.281, 1)}";
                }
            }
            catch (FeatureNotSupportedException fnsEx)
            {
                // Handle not supported on device exception
            }
            catch (FeatureNotEnabledException fneEx)
            {
                // Handle not enabled on device exception
            }
            catch (PermissionException pEx)
            {
                // Handle permission exception
            }
            catch (Exception ex)
            {
                // Unable to get location
            }
            return null;
        }

        protected override void OnDisappearing()
        {
            if (gpsCts != null && !gpsCts.IsCancellationRequested)
                gpsCts.Cancel();
            if (btCts != null && !btCts.IsCancellationRequested)
                btCts.Cancel();
            shouldStopTimer = true;
            DeviceDisplay.KeepScreenOn= false;
            base.OnDisappearing();
            Compass.Stop();
            Accelerometer.Stop();
        }
    }
}
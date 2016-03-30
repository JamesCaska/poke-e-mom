using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Microsoft.Maker.Serial;
using Microsoft.Maker.RemoteWiring;
using io.virtualbreadboard.api;
using Windows.UI.ViewManagement;

namespace PokeEmomApp
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        VbbIoTLoraStream vbbIotLoraStream;
        RemoteDevice arduino;

        /// <summary>
        /// Register a device in virtualbreadboard.io portal to automatically create these connection keys for your device.
        /// </summary>
        const string VBB_IO_APPEUI = "f8dc017950d84896";
        const string VBB_IO_DEVEUI = "a73590bdf4fcd407";
        const string VBB_IO_APPKEY = "f8dc017950d84896a73590bdf4fcd407";

#if DEBUG
        const int VBB_IO_POLLPERIOD_SECONDS = 5;
#else
        const int VBB_IO_POLLPERIOD_SECONDS = 30;
#endif

        public MainPage()
        {
            this.InitializeComponent();

            ApplicationView.GetForCurrentView().SetPreferredMinSize(new Size(300, 600));

            vbbIotLoraStream = new VbbIoTLoraStream(VBB_IO_APPEUI, VBB_IO_DEVEUI, VBB_IO_APPKEY, 5);
             
            //Firmata initialisation
            vbbIotLoraStream.AddFixedResponse( Convert.ToBase64String( new byte[] { 240, 107, 247 }), "8Gx/fwABAQF/AAEBAQMIfwABAQF/AAEBAQMIfwABAQEDCH8AAQEBfwABAQF/AAEBAQMIfwABAQEDCH8AAQEBAwh/AAEBAX8AAQEBfwABAQECCn8AAQEBAgp/AAEBAQIKfwABAQECCn8AAQEBAgp/AAEBAQIKf/c=");
             
            arduino = new RemoteDevice(vbbIotLoraStream);
 
            arduino.DeviceReady += OnConnectionEstablished;

            ((IStream)vbbIotLoraStream).begin(115200, SerialConfig.SERIAL_8N1);
       
        }
     
        private void OnConnectionEstablished()
        {
            //enable the buttons on the UI thread!
            var action = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, new Windows.UI.Core.DispatchedHandler(() => {
                PokeButton.IsEnabled = true;
               
            }));
        }
 
        /// <summary>
        /// POKE! There is a buzzer ( or other notification hardware ) attached to pin 13. All we have to do is turn it on
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnButton_Click(object sender, RoutedEventArgs e)
        {
            //Send a edge pulse to the notification hardware
            arduino.pinMode(13, PinMode.OUTPUT);
            arduino.digitalWrite(13, PinState.LOW);
            arduino.digitalWrite(13, PinState.HIGH);
 
        }

        private void OffButton_Click(object sender, RoutedEventArgs e)
        {
            
        }
         
    }
}

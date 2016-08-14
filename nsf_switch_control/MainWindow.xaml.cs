using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

using NationalInstruments;
using NationalInstruments.ModularInstruments.NISwitch;
using NationalInstruments.ModularInstruments.SystemServices.DeviceServices;
using NationalInstruments.Visa;
using NationalInstruments.DAQmx;

namespace NsfSwitchControl
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        TemperatureMeasurementController tempMeasCont;
        ImpedanceMeasurementController impMeasCont;
        SwitchController swCont;
        System.Windows.Threading.DispatcherTimer elapsedTimer;
        DateTime startDateTime;


        public MainWindow()
        {
            InitializeComponent();
            CheckHardwareStatus();
        }


        // TODO: write this to make sure all is well
        private void CheckHardwareStatus()
        {
            // check for switches, which should be the matrix switch
            ModularInstrumentsSystem modularInstrumentsSystem = new ModularInstrumentsSystem("NI-SWITCH");
            if (modularInstrumentsSystem.DeviceCollection.Count == 1)
            {
                labelSwitchConnectionStatus.Content = "PXIe-2529 128-Connection OK";
                labelSwitchConnectionStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green);
            }

            // check for DAQs, which should be temperature system
            var daq_channels = DaqSystem.Local.GetPhysicalChannels(PhysicalChannelTypes.AI, PhysicalChannelAccess.External);
            if (daq_channels.Count() == 20)
            {
                labelTemperatureConnectionStatus.Content = "PXIe-4537 20-Channel OK";
                labelTemperatureConnectionStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green);
            }

            // TODO: use NI-VISA to *IDN?\r the HM8118

        }


        private void InitializeControllers(string saveFileLocationFolder)
        {
            string saveFileLocation = saveFileLocationFolder + "/" + DateTime.Now.ToString("yyyy.MM.dd") + "-" + DateTime.Now.ToString("HH.mm");
            tempMeasCont = new TemperatureMeasurementController(saveFileLocation);
        }


        private void Button_Click(object sender, RoutedEventArgs e)
        {
            var fileDialog = new System.Windows.Forms.FolderBrowserDialog();
            var result = fileDialog.ShowDialog();
            if (result == System.Windows.Forms.DialogResult.OK)
                textboxFolderPath.Text = fileDialog.SelectedPath;
        }


        private void buttonInitializeControllers_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (textboxFolderPath.Text != "")
                {
                    InitializeControllers(textboxFolderPath.Text);
                    buttonInitializeControllers.IsEnabled = false;
                    buttonStartCollection.IsEnabled = true;
                    labelControllerStatus.Content = "Initialized";
                }
                else
                    System.Windows.MessageBox.Show("You forgot to choose a path!");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.ToString());
            }
        }


        private void buttonStartCollection_Click(object sender, RoutedEventArgs e)
        {
            tempMeasCont.StartMeasurement();
            startDateTime = DateTime.Now;
            elapsedTimer = new System.Windows.Threading.DispatcherTimer(new TimeSpan(0, 0, 1), System.Windows.Threading.DispatcherPriority.Normal, delegate
            {
                labelTimeElapsed.Content = (DateTime.Now.Subtract(startDateTime)).ToString(@"mm\:ss");
            }, this.Dispatcher);
            buttonStartCollection.IsEnabled = false;
            buttonStopCollection.IsEnabled = true;
            labelControllerStatus.Content = "Running...";
        }


        private void buttonStopCollection_Click(object sender, RoutedEventArgs e)
        {
            tempMeasCont.StopMeasurement();
            elapsedTimer.IsEnabled = false;
            buttonStopCollection.IsEnabled = false;
            buttonFlushSystem.IsEnabled = true;
            labelControllerStatus.Content = "Stopped";
        }


        private void buttonFlushSystem_Click(object sender, RoutedEventArgs e)
        {
            tempMeasCont = null;
            buttonInitializeControllers.IsEnabled = true;
            buttonFlushSystem.IsEnabled = false;
            labelControllerStatus.Content = "Flushed";
        }
    }

    // TODO: Write SwitchController class (which is NISwitch to the PXIe-2529)
    public class SwitchController
    {
        private void LoadSwitchDeviceNames()
        {
            ModularInstrumentsSystem modularInstrumentsSystem = new ModularInstrumentsSystem();//"NI-SWITCH");
            foreach (DeviceInfo device in modularInstrumentsSystem.DeviceCollection)
            {
                Console.WriteLine(device.Name);
            }
        }
    }


    // TODO: Write ImpedanceMeasurementController class (which is just VISA to the HM8118)
    public class ImpedanceMeasurementController
    { }


    public class TemperatureMeasurementController
    {
        // TODO: sync with the impedance reader so a temperature reading occurs the same time as a impedance measurement 
        // (I think if we just start both at the same time, since one is at 2 Hz, then the other should just switch within 0.25 seconds and have a reading at 0.5 seconds)
        // (of course it's largely dependent on the max speed of the LCR meter) 

        private AnalogMultiChannelReader analogInReader;
        private AsyncCallback myAsyncCallback;
        private NationalInstruments.DAQmx.Task myTask;
        private NationalInstruments.DAQmx.Task runningTask;
        private List<string> channelsToUseAddresses = new List<string>();
        System.IO.StreamWriter dataWriteFile;

        // RTD physical configuration
        static private List<int> __channelsToUse = new List<int>(Enumerable.Range(0, 20)); // use all of the channels
        static private string __pxiLocation = "PXI1Slot3";
        static private AIRtdType __rtdType = AIRtdType.Pt3851;
        static private double __r0Numeric = 100.0;
        static private AIResistanceConfiguration __resistanceConfiguration = AIResistanceConfiguration.ThreeWire;
        static private AIExcitationSource __excitationSource = AIExcitationSource.Internal;
        static private double __minimumValueNumeric = 0.0;
        static private double __maximumValueNumeric = 200.0;
        static private AITemperatureUnits __temperatureUnit = AITemperatureUnits.DegreesC;
        static private double __currentExcitationNumeric = 900e-6;

        // Sampling configuration
        static private double __sampleRate = 2.0; // in Hz
        static private int __samplesPerChannelBeforeRelease = 1;

        // Data table configuration
        static private string __dataTableHeader = "date,time," + String.Join(",", __channelsToUse);


        public TemperatureMeasurementController(string saveFileLocation)
        {
            foreach (int channelId in __channelsToUse)
                channelsToUseAddresses.Add(__pxiLocation + "/ai" + channelId.ToString());
            dataWriteFile = new System.IO.StreamWriter(saveFileLocation+".temperature.csv");
            dataWriteFile.WriteLine(__dataTableHeader);
        }


        public void StopMeasurement()
        {
            runningTask = null;
            if (myTask != null)
                myTask.Dispose();
        }


        // function called to start measurements
        public void StartMeasurement()
        {
            try
            {
                myTask = new NationalInstruments.DAQmx.Task();
                myAsyncCallback = new AsyncCallback(AnalogInCallback);

                // tell the PXIe-4537 to use the channels in channelsToUse
                foreach (string channel in channelsToUseAddresses)
                {
                    myTask.AIChannels.CreateRtdChannel(channel, "", __minimumValueNumeric, __maximumValueNumeric,
                        __temperatureUnit, __rtdType, __resistanceConfiguration, __excitationSource,
                        __currentExcitationNumeric, __r0Numeric);
                }

                // tell the PXIe-4537 to use its internal sample clock at some sample rate
                myTask.Timing.ConfigureSampleClock("", __sampleRate, SampleClockActiveEdge.Rising,
                    SampleQuantityMode.ContinuousSamples, __samplesPerChannelBeforeRelease);

                // check if the PXIe-4537 likes our settings
                myTask.Control(TaskAction.Verify);

                // declare the reader object for the task and prepare to run the task
                analogInReader = new AnalogMultiChannelReader(myTask.Stream);
                runningTask = myTask;

                // Use SynchronizeCallbacks to specify that the object 
                // marshals callbacks across threads appropriately.
                analogInReader.SynchronizeCallbacks = true;
                analogInReader.BeginReadWaveform(__samplesPerChannelBeforeRelease, myAsyncCallback, myTask);
            }
            catch (DaqException exception)
            {
                ErrorCatcher(exception);
            }
        }


        // function called by async when data comes in from the PXIe-4537
        // basically it just reads and then writes to the file
        // TODO: fix the problem where stop seems to mess up the writing, or at least the Async is mad slow
        private void AnalogInCallback(IAsyncResult ar)
        {
            try
            {
                if (runningTask != null && runningTask == ar.AsyncState)
                {
                    AnalogWaveform<double>[] data = analogInReader.EndReadWaveform(ar);
                    DataWrite(data);
                    analogInReader.BeginMemoryOptimizedReadWaveform(__samplesPerChannelBeforeRelease, myAsyncCallback, myTask, data);
                }
            }
            catch (DaqException exception)
            {
                ErrorCatcher(exception);
            }
        }


        // convert the data from the AnalogWaveform to a string to write
        private void DataWrite(AnalogWaveform<double>[] sourceArray)
        {
            try
            {
                if (sourceArray.Length != __channelsToUse.Count)
                    throw new System.DataMisalignedException("Error: the incoming data has a different size than the number of channels!");

                string currentDate= DateTime.Now.ToString("yyyy-MM-dd");
                string currentTime = DateTime.Now.ToString("hh:mm:ss.fff");

                // assume sourceArray has channels in the original order
                foreach (int sample in Enumerable.Range(0, __samplesPerChannelBeforeRelease))
                {
                    string currentLine = currentDate + "," + currentTime + ",";
                    double[] sampleData = new double[__channelsToUse.Count];
                    foreach (int channel in __channelsToUse)
                    {
                        sampleData[channel] = sourceArray[channel].Samples[sample].Value;
                    }
                    currentLine += String.Join(",", sampleData);
                    dataWriteFile.WriteLine(currentLine);
                }
            }
            catch (System.DataMisalignedException dmex)
            {
                System.Windows.MessageBox.Show(dmex.Message);
            }
        }


        // generic error handler to display any messages from DAQmx
        private void ErrorCatcher(DaqException exception)
        {
            System.Windows.MessageBox.Show(exception.Message);
            myTask.Dispose();
            runningTask = null;
        }
    }
}

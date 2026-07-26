using DriveVisualizer_App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;

namespace DriveVisualizer_App;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel { get; }

    public MainPage()
    {
        ViewModel = new MainViewModel(DispatcherQueue);
        InitializeComponent();
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
            return;

        if (!ViewModel.Targets.Contains(folder.Path))
            ViewModel.Targets.Add(folder.Path);
        ViewModel.SelectedTarget = folder.Path;
    }

    private void Chevron_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is NodeRow row)
            ViewModel.ToggleExpand(row);
    }

    private void TreeList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is NodeRow row)
            ViewModel.ToggleExpand(row);
    }
}

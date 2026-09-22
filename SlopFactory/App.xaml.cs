namespace SlopFactory;
public partial class App : Application { public App() => InitializeComponent(); protected override Window CreateWindow(IActivationState? state) => new(new MainPage()); }

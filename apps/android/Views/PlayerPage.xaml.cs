namespace StreamedTv.Views
{
    [QueryProperty(nameof(StreamUrl), "url")]
    [QueryProperty(nameof(StreamTitle), "title")]
    public partial class PlayerPage : ContentPage
    {
        private string? _streamUrl;
        private string? _streamTitle;

        public string? StreamUrl
        {
            set
            {
                _streamUrl = value;
                if (!string.IsNullOrEmpty(value))
                    Player.Source = new UrlWebViewSource { Url = value };
            }
        }

        public string? StreamTitle
        {
            set
            {
                _streamTitle = value;
                StreamTitleLabel.Text = value ?? "Stream";
            }
        }

        public PlayerPage()
        {
            InitializeComponent();
        }

        private async void OnBackClicked(object sender, EventArgs e)
        {
            await Shell.Current.GoToAsync("..");
        }
    }
}

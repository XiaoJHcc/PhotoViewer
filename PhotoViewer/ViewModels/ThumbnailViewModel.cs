using System.Collections.ObjectModel;
using ReactiveUI;
using System.Linq;
using System.Threading.Tasks;
using PhotoViewer.Core;

namespace PhotoViewer.ViewModels
{
    public class ThumbnailViewModel : ReactiveObject
    {
        public MainViewModel Main { get; }
        
        // public ReadOnlyObservableCollection<ImageFile> DisplayedFiles { get; }
        
        public ThumbnailViewModel(MainViewModel main)
        {
            Main = main;
            // DisplayedFiles = Main.FilteredFiles;
        }
    }
}
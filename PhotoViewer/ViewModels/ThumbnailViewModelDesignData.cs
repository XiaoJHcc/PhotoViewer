namespace PhotoViewer.ViewModels;

public class ThumbnailViewModelDesignData : ThumbnailViewModel
{
    public ThumbnailViewModelDesignData()
    {
        ThumbnailItems.Add(new ThumbnailItem 
        { 
            File = null,
            IsCurrent = true,
            Image = null // 初始为null，显示文件名
        });
    }
}
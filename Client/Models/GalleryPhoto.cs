namespace SmallBusinessSite.Client.Models;

public record GalleryPhoto(
    string Src,
    string Alt,
    string Title,
    string Caption,
    string ObjectPosition = "center center");

namespace cd_newsfeed.Models;

public class NewsFetcherState
{
    public int Id { get; set; } = 1;
    public bool IsEnabled { get; set; } = true;
    public bool DefaultSourcesSeeded { get; set; }
}

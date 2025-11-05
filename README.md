## YouTube Channel Scraper GUI

This project provides a Tkinter desktop application that collects metadata for the
videos uploaded to a YouTube channel using the YouTube Data API v3. The gathered
information is stored in a CSV file and written to disk.

### Requirements

Install the dependencies (ideally in a virtual environment):

```bash
pip install -r requirements.txt
```

You will also need a YouTube Data API v3 key. Set it in your environment as
`YOUTUBE_API_KEY` or paste it directly into the GUI.

### Running the application

```bash
python youtube_scraper_gui.py
```

Once the window appears:

1. Enter your API key (pre-filled if `YOUTUBE_API_KEY` is available).
2. Provide the channel URL or @handle. The scraper understands `/channel/`,
   `/user/`, custom `/c/` URLs, standard video links, shortened `youtu.be`
   links, and @handles.
3. Choose how many recent videos to fetch and where to save the CSV file.
4. Click **Start Scraping** and monitor the progress log for updates.

When the process completes the CSV will be saved in the selected folder.

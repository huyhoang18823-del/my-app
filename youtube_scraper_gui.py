"""Tkinter desktop app for scraping YouTube video metadata using the YouTube Data API v3."""

from __future__ import annotations

import csv
import os
import queue
import re
import threading
from dataclasses import dataclass
from datetime import datetime
from typing import Dict, Iterable, List, Optional
from urllib.parse import parse_qs, urlparse

import tkinter as tk
from tkinter import filedialog, messagebox, ttk
from tkinter.scrolledtext import ScrolledText

from googleapiclient.discovery import build
from googleapiclient.errors import HttpError

ISO_DURATION_RE = re.compile(
    r"PT"
    r"(?:(?P<hours>\d+)H)?"
    r"(?:(?P<minutes>\d+)M)?"
    r"(?:(?P<seconds>\d+)S)?"
)


def parse_iso8601_duration(duration: str) -> Optional[int]:
    """Convert an ISO 8601 duration string (e.g. PT1H2M3S) to seconds."""

    if not duration:
        return None
    match = ISO_DURATION_RE.fullmatch(duration)
    if not match:
        return None
    hours = int(match.group("hours") or 0)
    minutes = int(match.group("minutes") or 0)
    seconds = int(match.group("seconds") or 0)
    return hours * 3600 + minutes * 60 + seconds


def is_short_video(duration_seconds: Optional[int]) -> bool:
    """Return True when the duration qualifies as a YouTube Short."""

    return bool(duration_seconds is not None and duration_seconds <= 61)


def build_downsub_caption_url(video_id: str) -> str:
    """Construct the Downsub caption download URL for the video."""

    return f"https://downsub.com/?url=https://www.youtube.com/watch?v={video_id}"


@dataclass
class VideoMetadata:
    """Normalized representation of a YouTube video's metadata."""

    video_id: str
    data: Dict[str, Optional[str]]

    def to_csv_row(self) -> Dict[str, Optional[str]]:
        return self.data


class YouTubeDataClient:
    """Wrapper around the YouTube Data API v3 for channel scraping."""

    def __init__(self, api_key: str, logger):
        self.api_key = api_key
        self.logger = logger
        self.youtube = build("youtube", "v3", developerKey=api_key)

    def fetch_channel_videos(self, channel_reference: str, max_videos: int) -> List[VideoMetadata]:
        """Retrieve up to ``max_videos`` videos for the given channel reference."""

        channel_id = self._resolve_channel_id(channel_reference)
        if not channel_id:
            raise ValueError("Unable to resolve channel ID from the provided URL or handle.")

        self.logger(f"Resolved channel ID: {channel_id}")
        uploads_playlist_id, channel_title = self._get_uploads_playlist(channel_id)
        self.logger(f"Uploads playlist: {uploads_playlist_id}")

        video_ids: List[str] = []
        next_page_token: Optional[str] = None
        while len(video_ids) < max_videos:
            remaining = max_videos - len(video_ids)
            page = (
                self.youtube.playlistItems()
                .list(
                    part="contentDetails",
                    playlistId=uploads_playlist_id,
                    maxResults=min(50, remaining),
                    pageToken=next_page_token,
                )
                .execute()
            )

            for item in page.get("items", []):
                video_id = item.get("contentDetails", {}).get("videoId")
                if video_id:
                    video_ids.append(video_id)

            self.logger(f"Fetched {len(video_ids)} / {max_videos} video ids ...")

            next_page_token = page.get("nextPageToken")
            if not next_page_token:
                break

        if not video_ids:
            self.logger("No videos found in uploads playlist.")
            return []

        videos: List[VideoMetadata] = []
        for start in range(0, len(video_ids), 50):
            chunk_ids = video_ids[start : start + 50]
            self.logger(
                f"Downloading metadata for videos {start + 1}-{start + len(chunk_ids)}"
            )
            response = (
                self.youtube.videos()
                .list(part="snippet,contentDetails,statistics", id=",".join(chunk_ids))
                .execute()
            )

            for item in response.get("items", []):
                videos.append(self._parse_video(item, channel_title))

        return videos

    def _resolve_channel_id(self, reference: str) -> Optional[str]:
        """Resolve a channel ID from a URL, handle, user name, or raw ID."""

        ref = reference.strip()
        if not ref:
            return None

        if ref.startswith("UC") and len(ref) >= 24:
            return ref

        if ref.startswith("@"):
            return self._resolve_handle(ref)

        if ref.startswith("http"):
            parsed = urlparse(ref)
            path = parsed.path.strip("/")
            segments = path.split("/") if path else []

            if segments:
                first = segments[0]
                if first.startswith("@"):
                    return self._resolve_handle(first)
                if first == "channel" and len(segments) > 1:
                    return segments[1]
                if first == "user" and len(segments) > 1:
                    return self._resolve_username(segments[1])
                if first == "c" and len(segments) > 1:
                    return self._resolve_custom_url(segments[1])
                if first in {"watch", "shorts"}:
                    video_id = None
                    if first == "watch":
                        video_id = parse_qs(parsed.query).get("v", [None])[0]
                    elif len(segments) > 1:
                        video_id = segments[1]
                    if video_id:
                        return self._resolve_from_video(video_id)
            if parsed.netloc == "youtu.be" and segments:
                return self._resolve_from_video(segments[-1])

        # Fall back to username-style lookup and search
        user_candidate = ref.lstrip("@")
        return (
            self._resolve_username(user_candidate)
            or self._resolve_handle(f"@{user_candidate}")
            or self._resolve_custom_url(user_candidate)
        )

    def _resolve_handle(self, handle: str) -> Optional[str]:
        handle_value = handle if handle.startswith("@") else f"@{handle}"
        try:
            response = (
                self.youtube.channels()
                .list(part="id", forHandle=handle_value)
                .execute()
            )
            items = response.get("items", [])
            if items:
                return items[0].get("id")
        except HttpError as exc:
            self.logger(f"Handle lookup failed: {exc}")
        return self._resolve_custom_url(handle_value.lstrip("@"))

    def _resolve_username(self, username: str) -> Optional[str]:
        try:
            response = (
                self.youtube.channels()
                .list(part="id", forUsername=username)
                .execute()
            )
            items = response.get("items", [])
            if items:
                return items[0].get("id")
        except HttpError as exc:
            self.logger(f"Username lookup failed: {exc}")
        return None

    def _resolve_custom_url(self, query: str) -> Optional[str]:
        try:
            response = (
                self.youtube.search()
                .list(part="snippet", q=query, type="channel", maxResults=1)
                .execute()
            )
            items = response.get("items", [])
            if items:
                return items[0].get("snippet", {}).get("channelId")
        except HttpError as exc:
            self.logger(f"Custom URL lookup failed: {exc}")
        return None

    def _resolve_from_video(self, video_id: str) -> Optional[str]:
        try:
            response = (
                self.youtube.videos()
                .list(part="snippet", id=video_id)
                .execute()
            )
            items = response.get("items", [])
            if items:
                return items[0].get("snippet", {}).get("channelId")
        except HttpError as exc:
            self.logger(f"Video lookup failed: {exc}")
        return None

    def _get_uploads_playlist(self, channel_id: str) -> tuple[str, str]:
        response = (
            self.youtube.channels()
            .list(part="contentDetails,snippet", id=channel_id)
            .execute()
        )
        items = response.get("items", [])
        if not items:
            raise ValueError("Channel not found or missing uploads playlist.")
        channel = items[0]
        uploads_playlist = (
            channel.get("contentDetails", {})
            .get("relatedPlaylists", {})
            .get("uploads")
        )
        if not uploads_playlist:
            raise ValueError("Uploads playlist was not returned by the API.")
        channel_title = channel.get("snippet", {}).get("title", channel_id)
        return uploads_playlist, channel_title

    def _parse_video(self, item: Dict, channel_title: str) -> VideoMetadata:
        snippet = item.get("snippet", {})
        content_details = item.get("contentDetails", {})
        statistics = item.get("statistics", {})
        video_id = item.get("id", "")
        duration = content_details.get("duration")
        duration_seconds = parse_iso8601_duration(duration)

        data = {
            "videoId": video_id,
            "title": snippet.get("title", ""),
            "publishedAt": snippet.get("publishedAt", ""),
            "duration": duration or "",
            "duration_seconds": duration_seconds if duration_seconds is not None else "",
            "is_short": str(is_short_video(duration_seconds)),
            "viewCount": statistics.get("viewCount", ""),
            "likeCount": statistics.get("likeCount", ""),
            "commentCount": statistics.get("commentCount", ""),
            "tags": ", ".join(snippet.get("tags", [])),
            "categoryId": snippet.get("categoryId", ""),
            "defaultLanguage": snippet.get("defaultLanguage", ""),
            "defaultAudioLanguage": snippet.get("defaultAudioLanguage", ""),
            "channelTitle": snippet.get("channelTitle", channel_title),
            "url": f"https://www.youtube.com/watch?v={video_id}",
            "caption_downsub_url": build_downsub_caption_url(video_id),
            "description": snippet.get("description", ""),
        }
        return VideoMetadata(video_id=video_id, data=data)


class YouTubeScraperApp:
    """Tkinter GUI for collecting YouTube channel metadata."""

    CSV_FIELDS: Iterable[str] = (
        "videoId",
        "title",
        "publishedAt",
        "duration",
        "duration_seconds",
        "is_short",
        "viewCount",
        "likeCount",
        "commentCount",
        "tags",
        "categoryId",
        "defaultLanguage",
        "defaultAudioLanguage",
        "channelTitle",
        "url",
        "caption_downsub_url",
        "description",
    )

    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        self.root.title("YouTube Channel Scraper – Downsub API Style")
        self.root.geometry("820x640")
        self.root.minsize(700, 520)

        for index in range(3):
            self.root.rowconfigure(index, weight=0)
        self.root.rowconfigure(2, weight=1)
        self.root.columnconfigure(0, weight=1)

        self.api_key_var = tk.StringVar(value=os.environ.get("YOUTUBE_API_KEY", ""))
        self.channel_var = tk.StringVar()
        self.max_videos_var = tk.IntVar(value=50)
        self.folder_var = tk.StringVar(value=os.getcwd())

        self.log_queue: queue.Queue = queue.Queue()
        self.worker: Optional[threading.Thread] = None

        self._build_widgets()
        self.root.after(100, self._poll_log_queue)

    def _build_widgets(self) -> None:
        form = ttk.Frame(self.root, padding=(20, 20, 20, 10))
        form.grid(row=0, column=0, sticky="nsew")
        form.columnconfigure(1, weight=1)

        ttk.Label(form, text="YouTube Data API v3 Key:").grid(row=0, column=0, sticky="w")
        api_entry = ttk.Entry(form, textvariable=self.api_key_var)
        api_entry.grid(row=0, column=1, sticky="ew", padx=(10, 0))

        ttk.Label(form, text="YouTube Channel URL or Handle:").grid(
            row=1, column=0, sticky="w", pady=(10, 0)
        )
        channel_entry = ttk.Entry(form, textvariable=self.channel_var)
        channel_entry.grid(row=1, column=1, sticky="ew", padx=(10, 0), pady=(10, 0))

        ttk.Label(form, text="Max Videos:").grid(row=2, column=0, sticky="w", pady=(10, 0))
        max_spin = ttk.Spinbox(
            form,
            from_=1,
            to=500,
            textvariable=self.max_videos_var,
            width=10,
        )
        max_spin.grid(row=2, column=1, sticky="w", padx=(10, 0), pady=(10, 0))

        ttk.Label(form, text="Save Folder:").grid(row=3, column=0, sticky="w", pady=(10, 0))
        folder_frame = ttk.Frame(form)
        folder_frame.grid(row=3, column=1, sticky="ew", padx=(10, 0), pady=(10, 0))
        folder_frame.columnconfigure(0, weight=1)
        folder_entry = ttk.Entry(folder_frame, textvariable=self.folder_var)
        folder_entry.grid(row=0, column=0, sticky="ew")
        ttk.Button(folder_frame, text="Browse...", command=self._choose_folder).grid(
            row=0, column=1, padx=(8, 0)
        )

        button_frame = ttk.Frame(self.root, padding=(20, 0, 20, 10))
        button_frame.grid(row=1, column=0, sticky="ew")
        button_frame.columnconfigure(0, weight=1)
        self.start_button = tk.Button(
            button_frame,
            text="Start Scraping",
            command=self.start_scraping,
            bg="#1f2933",
            fg="white",
            activebackground="#111827",
            activeforeground="white",
            relief=tk.FLAT,
            height=2,
        )
        self.start_button.grid(row=0, column=0, sticky="ew")

        log_frame = ttk.Frame(self.root, padding=(20, 0, 20, 10))
        log_frame.grid(row=2, column=0, sticky="nsew")
        log_frame.rowconfigure(0, weight=1)
        log_frame.columnconfigure(0, weight=1)

        ttk.Label(log_frame, text="Progress Log:").grid(row=0, column=0, sticky="w")
        self.log_text = ScrolledText(
            log_frame,
            height=15,
            state="disabled",
            background="#0f172a",
            foreground="#e2e8f0",
            insertbackground="#e2e8f0",
            wrap="word",
        )
        self.log_text.grid(row=1, column=0, sticky="nsew", pady=(6, 0))

        instructions = (
            "Enter your API key and channel URL, choose how many uploads to fetch, "
            "pick a folder for the CSV export, then press Start Scraping."
        )
        ttk.Label(
            self.root,
            text=instructions,
            padding=(20, 0, 20, 20),
            foreground="#475569",
            wraplength=760,
            justify="center",
        ).grid(row=3, column=0, sticky="ew")

    def _choose_folder(self) -> None:
        folder = filedialog.askdirectory(title="Select output folder")
        if folder:
            self.folder_var.set(folder)

    def start_scraping(self) -> None:
        if self.worker and self.worker.is_alive():
            messagebox.showinfo("Scraper", "Scraping is already running. Please wait.")
            return

        api_key = self.api_key_var.get().strip()
        channel_ref = self.channel_var.get().strip()
        folder = self.folder_var.get().strip()
        max_videos = self.max_videos_var.get()

        if not api_key:
            messagebox.showerror("Scraper", "API key is required.")
            return
        if not channel_ref:
            messagebox.showerror("Scraper", "Channel URL or handle is required.")
            return
        if max_videos <= 0:
            messagebox.showerror("Scraper", "Max videos must be a positive number.")
            return
        if not folder:
            messagebox.showerror("Scraper", "Please choose a folder to store the CSV file.")
            return
        if not os.path.isdir(folder):
            messagebox.showerror("Scraper", "The selected folder does not exist.")
            return

        self._clear_log()
        self._append_log("Starting scrape...")
        self.start_button.config(state=tk.DISABLED)

        self.worker = threading.Thread(
            target=self._run_scraper,
            args=(api_key, channel_ref, max_videos, folder),
            daemon=True,
        )
        self.worker.start()

    def _run_scraper(self, api_key: str, channel_ref: str, max_videos: int, folder: str) -> None:
        try:
            client = YouTubeDataClient(api_key, self._queue_log)
            videos = client.fetch_channel_videos(channel_ref, max_videos)
            if not videos:
                self._queue_log("No videos were returned by the API.")
            output_path = self._write_csv(videos, folder)
            self.log_queue.put(("done", {"success": True, "path": output_path}))
        except HttpError as exc:
            self.log_queue.put(("done", {"success": False, "message": f"API error: {exc}"}))
        except Exception as exc:  # pylint: disable=broad-except
            self.log_queue.put(("done", {"success": False, "message": str(exc)}))

    def _write_csv(self, videos: List[VideoMetadata], folder: str) -> str:
        timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        filename = f"youtube_channel_export_{timestamp}.csv"
        path = os.path.join(folder, filename)
        with open(path, "w", newline="", encoding="utf-8") as csvfile:
            writer = csv.DictWriter(csvfile, fieldnames=list(self.CSV_FIELDS))
            writer.writeheader()
            for video in videos:
                writer.writerow(video.to_csv_row())
        self._queue_log(f"Saved CSV to {path}")
        return path

    def _queue_log(self, message: str) -> None:
        self.log_queue.put(("log", message))

    def _poll_log_queue(self) -> None:
        try:
            while True:
                kind, payload = self.log_queue.get_nowait()
                if kind == "log":
                    self._append_log(payload)
                elif kind == "done":
                    self.start_button.config(state=tk.NORMAL)
                    if payload.get("success"):
                        self._append_log("Scraping finished successfully.")
                        path = payload.get("path", "")
                        if path:
                            messagebox.showinfo(
                                "Scraper",
                                f"Scraping completed. CSV saved to:\n{path}",
                            )
                    else:
                        error_message = payload.get("message", "Scraping failed.")
                        self._append_log(f"ERROR: {error_message}")
                        messagebox.showerror("Scraper", error_message)
        except queue.Empty:
            pass
        finally:
            self.root.after(120, self._poll_log_queue)

    def _append_log(self, message: str) -> None:
        self.log_text.config(state="normal")
        self.log_text.insert(tk.END, message + "\n")
        self.log_text.see(tk.END)
        self.log_text.config(state="disabled")

    def _clear_log(self) -> None:
        self.log_text.config(state="normal")
        self.log_text.delete("1.0", tk.END)
        self.log_text.config(state="disabled")


def main() -> None:
    root = tk.Tk()
    app = YouTubeScraperApp(root)
    root.mainloop()


if __name__ == "__main__":
    main()

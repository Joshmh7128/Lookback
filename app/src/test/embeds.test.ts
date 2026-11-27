import assert from "node:assert/strict";
import { test } from "node:test";
import { parseEmbedSource, parseStart, validateEmbedSource, videoRef } from "../main/embeds";

test("YouTube links of every common shape give the video id and start time", () => {
  const id = "M7lc1UVf-VE";
  for (const link of [`https://www.youtube.com/watch?v=${id}`, `youtube.com/watch?v=${id}&list=x`, `https://youtu.be/${id}`, `https://m.youtube.com/shorts/${id}`, `https://www.youtube.com/live/${id}`, `https://www.youtube-nocookie.com/embed/${id}`]) {
    assert.deepEqual(parseEmbedSource(link), { platform: "youtube", videoId: id, startSeconds: 0 }, link);
  }

  assert.deepEqual(parseEmbedSource(`https://youtu.be/${id}?t=1m30s`), { platform: "youtube", videoId: id, startSeconds: 90 });
});

test("Twitch channels and videos", () => {
  assert.deepEqual(parseEmbedSource("https://www.twitch.tv/SomeChannel"), { platform: "twitch", kind: "channel", channel: "somechannel" });
  assert.deepEqual(parseEmbedSource("twitch.tv/videos/123456789"), { platform: "twitch", kind: "video", videoId: "123456789" });
  assert.equal(parseEmbedSource("https://www.twitch.tv/directory"), null);
  assert.equal(parseEmbedSource("https://www.twitch.tv/somechannel/clips"), null);
});

test("anything else is refused", () => {
  for (const link of ["", "hello", "javascript:alert(1)", "file:///C:/x", "https://evil.example/watch?v=M7lc1UVf-VE", "https://www.youtube.com/watch?v=short", "https://youtube.com.evil.example/watch?v=M7lc1UVf-VE", "x".repeat(5000)]) {
    assert.equal(parseEmbedSource(link), null, link.slice(0, 60));
  }
});

test("start times", () => {
  assert.equal(parseStart("90"), 90);
  assert.equal(parseStart("1h2m3s"), 3723);
  assert.equal(parseStart("garbage"), 0);
  assert.equal(parseStart(null), 0);
});

test("sources from the page are re-validated", () => {
  assert.deepEqual(validateEmbedSource({ platform: "youtube", videoId: "M7lc1UVf-VE", startSeconds: -4 }), { platform: "youtube", videoId: "M7lc1UVf-VE", startSeconds: 0 });
  assert.equal(validateEmbedSource({ platform: "youtube", videoId: "../../etc" }), null);
  assert.equal(validateEmbedSource({ platform: "twitch", kind: "channel", channel: "a b" }), null);
  assert.equal(validateEmbedSource("youtube"), null);
});

test("videoRef names the source", () => {
  assert.equal(videoRef({ platform: "youtube", videoId: "M7lc1UVf-VE", startSeconds: 0 }), "M7lc1UVf-VE");
  assert.equal(videoRef({ platform: "twitch", kind: "channel", channel: "x" }), "channel:x");
  assert.equal(videoRef({ platform: "twitch", kind: "video", videoId: "1" }), "video:1");
});

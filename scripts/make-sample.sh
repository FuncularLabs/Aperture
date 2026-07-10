#!/usr/bin/env bash
# Generates the packaged demo library under sample/.
#  - Downloaded photos: Lorem Picsum (https://picsum.photos), Unsplash license (free to use).
#  - Synthetic images/video: original, generated with ImageMagick + ffmpeg (no license concerns).
# EXIF is stripped and mtimes are set so the app's date sections are tidy and deterministic.
# Most items live in the root (a rich date-sorted home view); a couple of subfolders show the tree.
set -e
cd "$(dirname "$0")/.."
ROOT="sample"
rm -rf "$ROOT"
mkdir -p "$ROOT" "$ROOT/screenshots" "$ROOT/trips"

dl() { curl -sSL --max-time 40 -o "$4" "https://picsum.photos/seed/$1/$2/$3"; magick "$4" -strip "$4" 2>/dev/null || true; }
syn() { magick "$@"; }
stamp() { touch -t "$1" "$2"; }

# ---- Root: downloaded free-license photos, spread across dates ----
dl ap-meadow   1600 1066 "$ROOT/2021-04-18 09.14.22.jpg"; stamp 202104180914.22 "$ROOT/2021-04-18 09.14.22.jpg"
dl ap-coast    1600 1066 "$ROOT/2021-08-03 18.42.10.jpg"; stamp 202108031842.10 "$ROOT/2021-08-03 18.42.10.jpg"
dl ap-portrait 1066 1600 "$ROOT/2022-02-11 12.05.51.jpg"; stamp 202202111205.51 "$ROOT/2022-02-11 12.05.51.jpg"
dl ap-city     1600 1066 "$ROOT/2022-06-27 21.33.08.jpg"; stamp 202206272133.08 "$ROOT/2022-06-27 21.33.08.jpg"
dl ap-forest   1400 1400 "$ROOT/2022-10-09 15.20.44.jpg"; stamp 202210091520.44 "$ROOT/2022-10-09 15.20.44.jpg"
dl ap-desert   1600 1066 "$ROOT/2023-03-22 07.58.19.jpg"; stamp 202303220758.19 "$ROOT/2023-03-22 07.58.19.jpg"
dl ap-lake     1920 1080 "$ROOT/2023-09-14 17.46.37.jpg"; stamp 202309141746.37 "$ROOT/2023-09-14 17.46.37.jpg"
dl ap-street   1066 1600 "$ROOT/2024-01-07 20.02.55.jpg"; stamp 202401072002.55 "$ROOT/2024-01-07 20.02.55.jpg"
dl ap-market   1600 1066 "$ROOT/2024-04-19 13.27.41.jpg"; stamp 202404191327.41 "$ROOT/2024-04-19 13.27.41.jpg"
dl ap-harbor   1600 1066 "$ROOT/2025-07-21 19.09.28.jpg"; stamp 202507211909.28 "$ROOT/2025-07-21 19.09.28.jpg"

# ---- Root: synthetic images, spread across dates ----
syn -size 1280x854 plasma:fractal                                    "$ROOT/2021-12-15 10.30.00.jpg"; stamp 202112151030.00 "$ROOT/2021-12-15 10.30.00.jpg"
syn -size 1200x1200 radial-gradient:#f6d365-#fda085                   "$ROOT/2023-05-30 11.11.02.jpg"; stamp 202305301111.02 "$ROOT/2023-05-30 11.11.02.jpg"
syn -size 1400x900 -define gradient:angle=45 gradient:#1e3c72-#2a5298 "$ROOT/2023-11-28 12.00.00.jpg"; stamp 202311281200.00 "$ROOT/2023-11-28 12.00.00.jpg"
syn -size 1500x1000 -define gradient:angle=135 gradient:#0f2027-#2c5364 "$ROOT/2024-08-30 19.45.00.jpg"; stamp 202408301945.00 "$ROOT/2024-08-30 19.45.00.jpg"
syn -size 1200x1200 plasma:fractal -blur 0x2 -modulate 100,140        "$ROOT/2025-02-14 16.00.00.jpg"; stamp 202502141600.00 "$ROOT/2025-02-14 16.00.00.jpg"
syn -size 900x1350 -define gradient:angle=20 gradient:#ff9a9e-#a18cd1  "$ROOT/2025-11-06 08.15.00.jpg"; stamp 202511060815.00 "$ROOT/2025-11-06 08.15.00.jpg"

# ---- Root: synthetic video ----
ffmpeg -v error -y -f lavfi -i "mandelbrot=size=1280x720:rate=25" -t 6 -pix_fmt yuv420p -c:v libx264 -crf 28 \
  "$ROOT/2024-06-12 17.12.00.mp4"; stamp 202406121712.00 "$ROOT/2024-06-12 17.12.00.mp4"

# ---- Subfolders (demo the left tree + navigation) ----
dl ap-hills    1500 1000 "$ROOT/trips/2025-05-02 08.33.12.jpg"; stamp 202505020833.12 "$ROOT/trips/2025-05-02 08.33.12.jpg"
dl ap-canyon   1600 1066 "$ROOT/trips/2025-05-03 14.19.40.jpg"; stamp 202505031419.40 "$ROOT/trips/2025-05-03 14.19.40.jpg"
syn -size 1000x1000 plasma:tomato-steelblue                          "$ROOT/screenshots/plasma-warmcool.jpg"; stamp 202311281200.00 "$ROOT/screenshots/plasma-warmcool.jpg"
syn -size 1600x900 plasma:seagreen-midnightblue                      "$ROOT/screenshots/wide-plasma.jpg";     stamp 202601240815.00 "$ROOT/screenshots/wide-plasma.jpg"

echo "generated $(find "$ROOT" -type f | wc -l) files, $(du -sh "$ROOT" | cut -f1) total"

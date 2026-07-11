// Several tests generate real thumbnails (SkiaSharp / ffmpeg / shell decodes). Under xUnit's default
// parallel execution those decodes contend and a count assertion occasionally trips. The whole suite
// runs in ~1s, so run test collections sequentially for deterministic results — important for CI.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

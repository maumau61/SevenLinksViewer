---
name: "Clip won't play (new firmware?)"
about: A clip fails with 'failed to play clip' or 'hevc detected only with low score'
title: "[firmware] Clip won't play — "
labels: firmware
---

**Camera model / firmware**
<!-- e.g. 7links WK-xxxx, firmware date if known -->

**What happens**
<!-- e.g. app shows 'failed to play clip', status bar text, dialog message -->

**Probe output**
Run this on one failing event folder and paste the output (especially the first
32/48 bytes and the reported `byte[12]` value):

```
dotnet run --project src/MediaProbe -- "path\to\failing-event-folder"
```

```
<paste probe output here>
```

**Does older footage still play?**
<!-- yes / no — helps confirm it's a firmware header change -->

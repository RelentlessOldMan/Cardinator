# Example 04 · Bring your own frame

**Have your own frame art? Turn it into a template.** Give Cardinator a transparent PNG (see-through
where the art and text should show) and it creates a template you can pick from the Frame dropdown.

## What's here

```
04-custom-frame/
  frame/gilded.png   <- our hand-made frame (transparent center + ornate border)
  card.json                 <- a card that uses the new template
  art/hero.png              <- placeholder artwork
  output/warden.png         <- the rendered result (checked in)
```

## 1. Import the frame as a template

**App:** next to the Frame dropdown, click **Import…** and choose your PNG.

**Command line:** (`fullart` makes the art fill the card with text on top; drop it for a windowed frame)

```powershell
Cardinator.exe --newtemplate "Gilded" examples/04-custom-frame/frame/gilded.png fullart
```

This writes a `template.json` next to your frame under `CardinatorData/templates/gilded/`.
Open it to fine-tune where the title / type / text sit — see
[docs/TEMPLATES.md](../../docs/TEMPLATES.md).

## 2. Use it

Set a card's `template` to **Gilded** and render:

```powershell
Cardinator.exe --render examples/04-custom-frame/card.json examples/04-custom-frame/output/warden.png
```

## The output

Your frame, over the art, with the card text on top:

<p><img src="output/warden.png" width="320" alt="Card rendered on the custom Gilded frame"></p>

The frame PNG itself (transparent center — that's where the art shows through):

<p><img src="frame/gilded.png" width="200" alt="The custom frame PNG"></p>

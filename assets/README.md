# Preppie the Prepper - Design Assets

This directory contains all design assets for Preppie the Prepper, including avatars, icons, banners, and branding materials.

**Brand Guide**: See [docs/BRAND_GUIDE.md](../docs/BRAND_GUIDE.md) for complete brand guidelines, color palette, and typography.

---

## 📁 Directory Structure

```
assets/
├── avatars/              # Profile images and avatars ✅
├── icons/                # App icons (various sizes) ✅
├── teams/                # Microsoft Teams-specific assets ✅
├── banners/              # Hero images and feature banners
├── diagrams/             # Architecture and workflow diagrams
├── social/               # Social media assets
├── animations/           # Loading spinners and animations
├── brand/                # Brand guidelines and color palettes ✅
└── README.md             # This file
```

**Legend**: ✅ = Assets created | ⏳ = Needed | 🎨 = Draft/Placeholder

---

## 🎨 Brand Identity Summary

**Primary Colors:**
- **Lime Green**: `#84CC16` (KnowAll brand color)
- **Azure Blue**: `#0078D4` (Microsoft ecosystem)

**Typography:**
- Primary: Segoe UI
- Monospace: Cascadia Code

**Design Style:**
- Professional yet approachable
- Clean and modern
- Microsoft Teams/Azure alignment
- Preparation and organization theme

**Full Brand Guide**: [docs/BRAND_GUIDE.md](../docs/BRAND_GUIDE.md)

---

## 📦 Asset Inventory

### 1. Avatars & Profile Images

#### ✅ `avatars/preppie_avatar.png`
- **Size**: 512x512 pixels
- **Status**: 🎨 Draft placeholder (blue gradient with white "P")
- **Usage**: Teams bot, AI Foundry agent, documentation
- **Format**: PNG
- **Next**: Replace with professional avatar featuring KnowAll lime green

#### ⏳ `avatars/preppie_avatar_round.png`
- **Size**: 512x512 pixels (circular mask)
- **Status**: Needed
- **Usage**: Teams profile, circular avatar displays

#### ⏳ `avatars/preppie_avatar_small.png`
- **Size**: 96x96 pixels
- **Status**: Needed
- **Usage**: Notifications, thumbnails, small displays

#### ⏳ `avatars/preppie_avatar_large.png`
- **Size**: 1024x1024 pixels
- **Status**: Needed
- **Usage**: Marketing materials, presentations, print

---

### 2. App Icons

#### ✅ `icons/preppie_icon_16.png` → `icons/preppie_icon_512.png`
- **Sizes**: 16, 32, 64, 128, 192, 512 pixels
- **Status**: ✅ Created (Phase 1 draft)
- **Design**: Lime green circle with white "P"
- **Usage**:
  - 16px: Favicon, browser tabs
  - 32px: Taskbar icons
  - 64px: Desktop shortcuts
  - 128px: App lists
  - 192px: Android, PWA manifest
  - 512px: App stores, large displays
- **Format**: PNG
- **Next**: Professional redesign with more detailed icon work

---

### 3. Microsoft Teams Assets ⭐ **Priority: High**

#### ✅ `teams/color_icon.png`
- **Size**: 192x192 pixels
- **Status**: ✅ Created (Phase 1 draft)
- **Design**: Lime green (#84CC16) circle with white "P"
- **Usage**: Teams app catalog, app details page
- **Format**: PNG, full color
- **Next**: Professional redesign with refined typography

#### ✅ `teams/outline_icon.png`
- **Size**: 32x32 pixels
- **Status**: ✅ Created (Phase 1 draft)
- **Design**: White "P" on transparent background
- **Usage**: Teams left navigation rail (dark backgrounds)
- **Format**: PNG, single color with transparency
- **Next**: Create proper outline/line-art version

#### ⏳ `teams/wide_logo.png`
- **Size**: 300x100 pixels
- **Status**: Needed for Teams app submission
- **Design**: Horizontal logo: Icon + "Preppie the Prepper" wordmark
- **Usage**: Teams app details page header
- **Format**: PNG

---

### 4. Banners & Hero Images

#### ⏳ `banners/preppie_hero.png`
- **Size**: 1920x600 pixels
- **Status**: Needed (Phase 2)
- **Usage**: GitHub README, documentation homepage
- **Content**: Preppie in action during Teams meeting
- **Style**: Dark background with lime green accents

#### ⏳ `banners/feature_meeting_capture.png`
- **Size**: 1200x630 pixels
- **Usage**: Feature showcase - Real-time meeting transcription
- **Style**: Screenshot with overlay text

#### ⏳ `banners/feature_devops_integration.png`
- **Size**: 1200x630 pixels
- **Usage**: Feature showcase - Azure DevOps work item creation
- **Style**: Split screen: Teams + DevOps board

#### ⏳ `banners/feature_tminus15.png`
- **Size**: 1200x630 pixels
- **Usage**: Feature showcase - T-Minus-15 methodology
- **Style**: Diagram: Epic → Feature → User Story hierarchy

---

### 5. Diagrams & Documentation

#### ⏳ `diagrams/architecture.svg`
- **Status**: Needed (Phase 2)
- **Usage**: SOLUTION_DESIGN.adoc architecture section
- **Content**: Teams → Preppie Agent → Azure Functions → DevOps API
- **Format**: SVG (scalable) + PNG export (1600x900)
- **Style**: Dark theme with lime green highlights

#### ⏳ `diagrams/workflow_meeting.png`
- **Size**: 1600x900 pixels
- **Usage**: Meeting workflow documentation
- **Content**: Join → Listen → Transcribe → Analyze → Create Work Items
- **Style**: Flowchart with icons

#### ⏳ `diagrams/workflow_work_items.png`
- **Size**: 1600x900 pixels
- **Usage**: Work item creation flow
- **Content**: Epic → Features → User Stories (with T-Minus-15)
- **Style**: Hierarchical tree diagram

#### ⏳ `diagrams/workflow_tminus15.png`
- **Size**: 1600x900 pixels
- **Usage**: T-Minus-15 methodology visualization
- **Content**: Methodology steps and decision points
- **Style**: Process diagram

---

### 6. Social Media Assets

#### ⏳ `social/og_default.png`
- **Size**: 1200x630 pixels (Open Graph standard)
- **Usage**: Default social share image (Twitter, LinkedIn, Facebook)
- **Content**: Preppie logo + tagline: "AI Meeting Assistant for Azure DevOps"

#### ⏳ `social/og_launch.png`
- **Size**: 1200x630 pixels
- **Usage**: Launch announcement share image
- **Content**: "Introducing Preppie the Prepper" with key features

#### ⏳ `social/social_avatar.png`
- **Size**: 400x400 pixels
- **Usage**: Twitter/LinkedIn profile images
- **Design**: Simplified circular avatar

#### ⏳ `social/linkedin_cover.png`
- **Size**: 1584x396 pixels
- **Usage**: LinkedIn company page banner
- **Content**: Preppie branding with KnowAll integration

#### ⏳ `social/twitter_banner.png`
- **Size**: 1500x500 pixels
- **Usage**: Twitter/X profile header
- **Content**: Preppie + T-Minus-15 branding

---

### 7. Status & State Icons

#### ⏳ `icons/status_listening.png`
- **Size**: 24x24 pixels
- **Usage**: Meeting status - Preppie actively listening
- **Icon**: Waveform or microphone
- **Color**: Lime green

#### ⏳ `icons/status_processing.png`
- **Size**: 24x24 pixels
- **Usage**: Processing transcript indicator
- **Icon**: Spinning dots or gear
- **Color**: Azure blue

#### ⏳ `icons/status_creating.png`
- **Size**: 24x24 pixels
- **Usage**: Creating work items indicator
- **Icon**: Plus icon or pencil
- **Color**: Lime green

#### ⏳ `icons/status_idle.png`
- **Size**: 24x24 pixels
- **Usage**: Idle/waiting state
- **Icon**: Clock or pause
- **Color**: Gray

#### ⏳ `icons/status_error.png`
- **Size**: 24x24 pixels
- **Usage**: Error state indicator
- **Icon**: X or exclamation triangle
- **Color**: Red (#EF4444)

---

### 8. Animations (Future Phase)

#### ⏳ `animations/preppie_spinner.gif`
- **Usage**: Loading indicator for async operations
- **Animation**: Rotating/pulsing lime green circle
- **Duration**: Seamless loop

#### ⏳ `animations/success_checkmark.gif`
- **Usage**: Success confirmation (work item created)
- **Animation**: Checkmark draw-in animation
- **Duration**: 600ms

#### ⏳ `animations/preppie_listening.json` (Lottie)
- **Usage**: Real-time listening state in meeting
- **Animation**: Sound wave visualization
- **Format**: Lottie JSON for web/mobile

#### ⏳ `animations/preppie_thinking.json` (Lottie)
- **Usage**: AI processing/analyzing state
- **Animation**: Pulsing brain or connected dots
- **Format**: Lottie JSON

---

## 🚀 Implementation Phases

### ✅ Phase 1: MVP (Complete!)

**Priority**: Launch-ready core assets

- [x] Avatar placeholder (512x512) - Blue gradient
- [x] Teams color icon (192x192) - Lime green draft ✅
- [x] Teams outline icon (32x32) - White P ✅
- [x] App icon set (16-512px) - All sizes ✅
- [x] Brand style guide document ✅
- [x] Asset directory structure ✅

**Status**: ✅ **COMPLETE** - Ready for Teams app submission (draft icons)

---

### ⏳ Phase 2: Professional Assets

**Priority**: Polish for public launch

- [ ] Professional avatar redesign (512x512)
- [ ] Professional Teams icons (color + outline)
- [ ] Avatar variations (round, small, large)
- [ ] Wide logo for Teams (300x100)
- [ ] Hero banner for documentation (1920x600)
- [ ] Architecture diagram (SVG)

**Timeline**: 1-2 weeks after Phase 1
**Deliverable**: Professional, production-ready branding

---

### ⏳ Phase 3: Marketing & Growth

**Priority**: External promotion

- [ ] Feature banners (3x 1200x630)
- [ ] Social media asset set (OG images, profile pics, banners)
- [ ] Status icons set (5x 24x24)
- [ ] Workflow diagrams (3x 1600x900)
- [ ] Screenshot templates
- [ ] Loading animations (GIF/Lottie)

**Timeline**: As needed for marketing campaigns
**Deliverable**: Complete marketing asset library

---

## 🎨 Design Guidelines

### Color Usage

**Primary Brand Color**: Lime Green `#84CC16`
- Use for: Primary actions, highlights, success states, brand elements
- Don't overuse: Use as accent (10-20% of design)

**Secondary Color**: Azure Blue `#0078D4`
- Use for: Secondary actions, info states, Microsoft integration visuals

**Semantic Colors**:
- Success: `#10B981` (Emerald green)
- Warning: `#F59E0B` (Amber)
- Error: `#EF4444` (Red)
- Info: `#3B82F6` (Blue)

### Typography

**Headings**: Segoe UI Semibold (600)
**Body**: Segoe UI Regular (400)
**Code**: Cascadia Code Regular (400)

### Icon Style

- **Stroke weight**: 2px for 24x24 icons
- **Corner radius**: 2px for rounded elements
- **Padding**: 2-3px from edges
- **Style**: Outline (not filled) - matches Microsoft Fluent UI

### Logo Requirements

**Minimum Sizes**:
- Horizontal logo: 120px width minimum
- Icon-only: 32px minimum (must be recognizable)

**Clear Space**: 20px padding on all sides

**Color Modes**:
- Full color (primary)
- White on dark
- Dark on light
- Monochrome

---

## 📝 Asset Naming Convention

Follow these patterns for consistency:

**Avatars**: `preppie_avatar_{variant}.png`
- Examples: `preppie_avatar_round.png`, `preppie_avatar_small.png`

**Icons**: `preppie_icon_{size}.png`
- Examples: `preppie_icon_16.png`, `preppie_icon_192.png`

**Teams**: `{type}_icon.png`
- Examples: `color_icon.png`, `outline_icon.png`, `wide_logo.png`

**Banners**: `{category}_{description}.png`
- Examples: `banner_hero.png`, `feature_meeting.png`

**Diagrams**: `{type}_{name}.{ext}`
- Examples: `workflow_meeting.svg`, `architecture.png`

**Status Icons**: `status_{state}.png`
- Examples: `status_listening.png`, `status_error.png`

---

## 🔄 Updating Assets

When replacing placeholder assets:

1. **Maintain exact dimensions** - Don't change specified sizes
2. **Keep filenames identical** - Code/docs reference these names
3. **Test on light & dark backgrounds** - Ensure visibility
4. **Optimize file sizes**:
   - PNG: Use TinyPNG or ImageOptim
   - SVG: Use SVGO
   - Target: <50KB per icon, <500KB per banner
5. **Update this README** - Change status from ⏳ to ✅
6. **Commit with descriptive message**: "Update: Professional Preppie avatar"

---

## 🛠️ Design Tools & Resources

### Recommended Tools
- **Figma** - Vector design, collaboration (preferred)
- **Adobe Illustrator** - Professional vector graphics
- **Affinity Designer** - Affordable alternative
- **Canva** - Quick social media assets
- **GIMP/Photoshop** - Raster editing

### Icon Resources
- **Microsoft Fluent UI Icons** - For Teams consistency
- **Heroicons** - Clean outline icons
- **Phosphor Icons** - Modern icon library

### Image Optimization
- **TinyPNG** - PNG compression (online)
- **ImageOptim** - Batch optimization (Mac)
- **Squoosh** - Image compression (Google, web-based)
- **SVGO** - SVG optimization

---

## ✅ Current Status

**Assets Created**: 10 / 40+
- ✅ Avatar placeholder
- ✅ Teams color icon (draft)
- ✅ Teams outline icon (draft)
- ✅ App icons (6 sizes)
- ✅ Brand style guide

**Phase 1**: ✅ **COMPLETE**
**Phase 2**: ⏳ Not started
**Phase 3**: ⏳ Not started

**Next Milestone**: Professional redesign of core assets (avatar, Teams icons)

---

## 📞 Questions & Support

**Brand Guidelines**: [docs/BRAND_GUIDE.md](../docs/BRAND_GUIDE.md)
**Solution Design**: [docs/SOLUTION_DESIGN.adoc](../docs/SOLUTION_DESIGN.adoc)
**GitHub Issues**: [GetPenn.ie/issues](https://github.com/bengweeks/GetPenn.ie/issues)

---

## 📜 License

All design assets are proprietary to the Preppie the Prepper project.

**Allowed**:
- ✅ Use in Preppie documentation and marketing
- ✅ Teams bot integration
- ✅ Presentations and demos

**Not Allowed**:
- ❌ Redistribution or resale
- ❌ Use in other products without permission
- ❌ Modification of brand identity without approval

---

**Last Updated**: 2025-10-11
**Version**: 1.0
**Status**: Phase 1 Complete ✅

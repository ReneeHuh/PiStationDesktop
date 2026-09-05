@{
    SchemaVersion = 1
    BaselineDate = '2026-09-02'

    Reference = @{
        Name = 'T3 Code'
        Commit = '9159b808d35a88e74fc91e11070f3270cdb321f9'
        LocalCheckoutCommit = 'bba79cc254b65969bde6b6bfc3032c3b5b9316ae'
        RelativePath = 'tests/PiStation.UiTests/References/t3code-updated-screenshot.webp'
        Sha256 = '8922305E84411D63C5E1626155F4CE901AB7297959BB8EC12E064B9C2B9488DB'
    }

    ReviewSizes = @(
        @{
            Name = 'Default'; Width = 1200; Height = 800
            SidebarWidth = 260; ReadingColumnMaxWidth = 720; WorkbenchPanelWidth = 420
        }
        @{
            Name = 'Large'; Width = 1440; Height = 900
            SidebarWidth = 260; ReadingColumnMaxWidth = 720; WorkbenchPanelWidth = 420
        }
        @{
            Name = 'FullHD'; Width = 1920; Height = 1080
            SidebarWidth = 260; ReadingColumnMaxWidth = 720; WorkbenchPanelWidth = 420
        }
    )

    ResponsiveLayouts = @(
        @{ Name = 'Narrow'; MinWidth = 0; MaxWidth = 719 }
        @{ Name = 'Compact'; MinWidth = 720; MaxWidth = 899 }
        @{ Name = 'Standard'; MinWidth = 900; MaxWidth = 1179 }
        @{ Name = 'Wide'; MinWidth = 1180; MaxWidth = 0 }
    )

    TextScaleProfiles = @(100, 150, 200)

    States = @(
        @{
            Name = 'empty'
            Runner = 'Invoke-DriverContract.ps1'
            Artifact = 'artifacts/driver-contract.png'
            NormalLaunch = $true
            Purpose = 'Normal packaged launch with an empty workspace conversation.'
        }
        @{
            Name = 'completed'
            Runner = 'Invoke-VerticalSlice.ps1'
            Artifact = 'artifacts/runs/*/vertical-slice.png'
            Purpose = 'Completed conversation with Markdown, tool activity, and turn metrics.'
        }
        @{
            Name = 'running-tool'
            Runner = 'Invoke-VerticalSlice.ps1'
            Artifact = 'artifacts/runs/*/activity-running.png'
            Purpose = 'Active turn with the running status and stop affordance visible.'
        }
        @{
            Name = 'tool-details'
            Runner = 'Invoke-VerticalSlice.ps1'
            Artifact = 'artifacts/runs/*/activity-expanded.png'
            Purpose = 'Expanded reasoning and tool details.'
        }
        @{
            Name = 'interaction'
            Runner = 'Invoke-InteractionSlice.ps1'
            Artifact = 'artifacts/interaction-runs/*/interaction-slice.png'
            Purpose = 'Resolved approval and structured-question controls.'
        }
        @{
            Name = 'recovery'
            Runner = 'Invoke-RecoverySlice.ps1'
            Artifact = 'artifacts/recovery-runs/*/recovery-slice.png'
            Purpose = 'Pi-specific recovery state after a child-process failure.'
        }
        @{
            Name = 'long-transcript-top'
            Runner = 'Invoke-InputAccessibilitySlice.ps1'
            Artifact = 'artifacts/input-accessibility-runs/*/transcript-top.png'
            Purpose = 'Long transcript scrolled to its oldest content.'
        }
        @{
            Name = 'long-transcript-bottom'
            Runner = 'Invoke-InputAccessibilitySlice.ps1'
            Artifact = 'artifacts/input-accessibility-runs/*/transcript-bottom.png'
            Purpose = 'Long transcript returned to the live edge.'
        }
        @{
            Name = 'attachment-empty'
            Runner = 'Invoke-DriverContract.ps1'
            Artifact = 'artifacts/driver-contract.png'
            Purpose = 'Empty attachment rail and attachment affordance in a normal launch.'
        }
        @{
            Name = 'draft'
            Runner = 'Invoke-DraftSlice.ps1'
            Artifact = 'artifacts/draft-runs/*/draft-slice.png'
            Purpose = 'Persisted composer draft and project-file mention state.'
        }
        @{
            Name = 'sidebar-collapsed'
            Runner = 'Invoke-DriverContract.ps1'
            Artifact = 'artifacts/sidebar-collapsed.png'
            Purpose = 'Collapsed 52px workspace rail with recovery affordance.'
        }
        @{
            Name = 'right-panel-open'
            Runner = 'Invoke-WorkbenchSlice.ps1'
            Artifact = 'artifacts/workbench-runs/*/workbench-open.png'
            Purpose = 'Persisted workbench chrome with the Changes surface selected.'
        }
        @{
            Name = 'files-workbench'
            Runner = 'Invoke-WorkbenchSlice.ps1'
            Artifact = 'artifacts/workbench-runs/*/files-workbench.png'
            Purpose = 'Searchable project-file workbench with a selected text preview.'
        }
        @{
            Name = 'changes-workbench'
            Runner = 'Invoke-WorkbenchSlice.ps1'
            Artifact = 'artifacts/workbench-runs/*/changes-workbench.png'
            Purpose = 'Git branch, changed-file states, and a selected working-tree diff.'
        }
        @{
            Name = 'terminal-workbench'
            Runner = 'Invoke-WorkbenchSlice.ps1'
            Artifact = 'artifacts/workbench-runs/*/terminal-workbench.png'
            Purpose = 'Host-owned terminal session with shell, lifecycle controls, command input, and streamed output.'
        }
        @{
            Name = 'preview-workbench'
            Runner = 'Invoke-WorkbenchSlice.ps1'
            Artifact = 'artifacts/workbench-runs/*/preview-workbench.png'
            Purpose = 'Embedded HTTP preview with compact browser navigation and a loaded local development page.'
        }
        @{
            Name = 'settings-open'
            Runner = 'Invoke-DriverContract.ps1'
            Artifact = 'artifacts/settings-open.png'
            NormalLaunch = $true
            Purpose = 'First-class settings shell without test-only diagnostics.'
        }
        @{
            Name = 'responsive-narrow'
            Runner = 'Invoke-CompatibilitySlice.ps1'
            Artifact = 'artifacts/compatibility-runs/*/responsive-narrow.png'
            Purpose = 'Narrow shell with the 52px sidebar rail and compact header actions.'
        }
        @{
            Name = 'responsive-compact'
            Runner = 'Invoke-CompatibilitySlice.ps1'
            Artifact = 'artifacts/compatibility-runs/*/responsive-compact.png'
            Purpose = 'Compact shell with the workbench overlay boundary preserved.'
        }
        @{
            Name = 'responsive-standard'
            Runner = 'Invoke-CompatibilitySlice.ps1'
            Artifact = 'artifacts/compatibility-runs/*/responsive-standard.png'
            Purpose = 'Standard shell before the workbench-docking breakpoint.'
        }
        @{
            Name = 'responsive-wide'
            Runner = 'Invoke-CompatibilitySlice.ps1'
            Artifact = 'artifacts/compatibility-runs/*/responsive-wide.png'
            Purpose = 'Wide shell with the full header and centered reading column.'
        }
        @{
            Name = 'responsive-maximized'
            Runner = 'Invoke-CompatibilitySlice.ps1'
            Artifact = 'artifacts/compatibility-runs/*/responsive-maximized.png'
            Purpose = 'Maximized shell with bounded reading and composer columns.'
        }
        @{
            Name = 'theme-light-text-150'
            Runner = 'Invoke-CompatibilitySlice.ps1'
            Artifact = 'artifacts/compatibility-runs/*/theme-light-text-150.png'
            Purpose = 'Persisted light theme with a 150 percent app-owned typography profile.'
        }
        @{
            Name = 'theme-dark-text-200'
            Runner = 'Invoke-CompatibilitySlice.ps1'
            Artifact = 'artifacts/compatibility-runs/*/theme-dark-text-200.png'
            Purpose = 'Dark theme with a 200 percent app-owned typography profile.'
        }
    )

    ForbiddenNormalLaunchAutomationIds = @(
        'SimulateTransportDropButton'
    )

    ThemeVariants = @('Dark', 'Light', 'HighContrast')
    InteractionStates = @(
        'disabled', 'hover', 'pressed', 'focus', 'selected', 'running', 'warning', 'error'
    )

    RequiredThemeColorKeys = @(
        'PiCanvasColor',
        'PiSidebarColor',
        'PiChromeColor',
        'PiSurfaceColor',
        'PiSurfaceElevatedColor',
        'PiWorkbenchColor',
        'PiControlSurfaceColor',
        'PiSurfaceHoverColor',
        'PiSurfaceSelectedColor',
        'PiMessageUserColor',
        'PiOverlayColor',
        'PiDividerColor',
        'PiBorderColor',
        'PiBorderStrongColor',
        'PiTextPrimaryColor',
        'PiTextSecondaryColor',
        'PiTextMutedColor',
        'PiAccentColor',
        'PiAccentHoverColor',
        'PiFocusColor',
        'PiWarningSurfaceColor',
        'PiCriticalSurfaceColor',
        'PiSuccessSurfaceColor',
        'PiRunningColor',
        'PiCompletedColor',
        'PiWaitingColor',
        'PiCriticalColor',
        'PiOfflineColor'
    )

    RequiredBrushKeys = @(
        'PiChromeBrush',
        'PiWorkbenchBrush',
        'PiMessageUserBrush',
        'PiOverlayBrush',
        'PiDividerBrush',
        'PiFocusBrush',
        'PiWarningSurfaceBrush',
        'PiCriticalSurfaceBrush',
        'PiSuccessSurfaceBrush'
    )

    RequiredMetricKeys = @(
        'PiSidebarWidth',
        'PiSidebarWidthValue',
        'PiSidebarCollapsedWidth',
        'PiSidebarMinWidth',
        'PiSidebarMaxWidth',
        'PiWorkspaceTopBarHeight',
        'PiReadingColumnWidth',
        'PiUserMessageMaxWidth',
        'PiWorkbenchPanelWidth',
        'PiWorkbenchPanelMinWidth',
        'PiWorkbenchPanelMaxWidth',
        'PiComposerMinHeight',
        'PiComposerMaxHeight',
        'PiMainGutter',
        'PiBreakpointNarrow',
        'PiBreakpointCompact',
        'PiBreakpointWorkbench',
        'PiMotionDurationShort',
        'PiMotionDurationNormal'
    )

    RequiredStyleKeys = @(
        'PiChromeSurfaceStyle',
        'PiWorkbenchSurfaceStyle',
        'PiToolbarSurfaceStyle',
        'PiSidebarRowStyle',
        'PiDisclosureRowStyle',
        'PiAssistantMessageStyle',
        'PiUserMessageStyle',
        'PiBannerStyle',
        'PiWarningBannerStyle',
        'PiCriticalBannerStyle',
        'PiSuccessBannerStyle',
        'PiComposerSurfaceStyle',
        'PiComposerToolbarStyle',
        'PiComposerTextBoxStyle',
        'PiComposerComboBoxStyle',
        'PiAttachmentChipStyle',
        'PiToolbarButtonStyle',
        'PiToolbarIconButtonStyle',
        'PiWorkbenchTabStyle',
        'PiCircularPrimaryButtonStyle'
    )
}

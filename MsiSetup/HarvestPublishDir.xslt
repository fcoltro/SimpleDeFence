<?xml version="1.0" encoding="utf-8"?>
<!--
  Post-processes heat.exe's harvest of the self-contained net10 publish output.

  REGENERATING Dependencies.wxs
  =============================
  1. Publish (from the repository root):

       dotnet publish SimpleDeFence/SimpleDeFence.csproj -c Release -r win-x64 -p:SelfContained=true -o SimpleDeFence/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish

     (-p:SelfContained=true is the same setting as the CLI's self-contained switch, spelled the
     one way an XML comment permits. SimpleDeFence.csproj already sets SelfContained and
     RuntimeIdentifier, so both flags are belt-and-braces rather than strictly required.)

     The explicit -o matters: SimpleDeFence.csproj sets AppendTargetFrameworkToOutputPath=false,
     so a bare `dotnet publish` would land in bin\Release\win-x64\publish instead, and the
     PublishDir constant in MsiSetup.wixproj would point at nothing.

  2. Harvest (from the MsiSetup directory; adjust the WiX Toolset path to the local install):

       heat.exe dir "..\SimpleDeFence\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish" -platform x64 -cg PublishedDependencies -gg -scom -sreg -srd -dr INSTALLDIR -var var.PublishDir -t HarvestPublishDir.xslt -out Dependencies.wxs

     -cg PublishedDependencies  names the generated ComponentGroup; Product.wxs's <Feature>
                                references exactly this Id.
     -gg                        write concrete component GUIDs into the generated file. Note these
                                are fresh GUIDs on every harvest, NOT stable ones - re-harvesting
                                re-identifies every component. That is safe here only because
                                Product.wxs schedules RemoveExistingProducts After='InstallValidate'
                                (an early major upgrade), so an upgrade uninstalls the old component
                                set wholesale before laying down the new one. `-ag` (author
                                Guid="*", resolved at compile time) is the more usual choice and
                                describes the same behaviour more honestly; either works.
     -scom -sreg                do not harvest COM/registry data; nothing here self-registers.
     -srd                       do not emit a wrapper <Directory> for the publish root, because
                                -dr INSTALLDIR already nests the harvest under Product.wxs's
                                INSTALLDIR. Sub-directories (the per-language satellite folders)
                                are still emitted, which is what we want.
     -var var.PublishDir        emit Source="$(var.PublishDir)\..." instead of absolute paths;
                                PublishDir is defined in MsiSetup.wixproj's DefineConstants.
     -t HarvestPublishDir.xslt  apply this transform (see below).

     Component bitness: heat's output leaves Win64 unset, and WiX v3 defaults an unattributed
     component's bitness from candle's -arch, which the wixproj supplies from /p:Platform. So the
     MSI must be built with /p:Platform=x64 to match the win-x64 payload - an x86 build would
     produce 32-bit components installing a 64-bit runtime into ProgramFilesFolder.
     If a given heat build rejects -platform (it is not accepted by every 3.x release of the dir
     harvester), it is safe to drop: -arch already supplies the default.

  WHAT THIS TRANSFORM DOES
  ========================
  Removes SimpleDeFence.exe from the harvest. It is the one published file that Product.wxs still
  authors by hand, because its <File> must keep the stable Id "TinyWallEXE" that
  !(bind.fileVersion.TinyWallEXE) and the five FileKey='TinyWallEXE' custom actions resolve
  against; heat generates unpredictable hash-based Ids. Without this exclusion the same target
  path would be installed by two different components, which is an ICE30 violation.

  WiX v3's heat has no -exclude switch, so an XSL transform is the supported way to filter its
  output. This file has NOT been executed - the WiX Toolset is not installed on the machine where
  it was written, so treat it as reviewed-but-unrun on the first real harvest.
-->
<xsl:stylesheet version="1.0"
                xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:wix="http://schemas.microsoft.com/wix/2006/wi"
                exclude-result-prefixes="wix">

  <xsl:output method="xml" indent="yes" />

  <!-- Identity transform: copy everything through unchanged by default. -->
  <xsl:template match="@*|node()">
    <xsl:copy>
      <xsl:apply-templates select="@*|node()" />
    </xsl:copy>
  </xsl:template>

  <!-- Index, by component Id, every Component holding the apphost. Keying by @Id lets the same
       lookup match the ComponentRef that Component's ComponentGroup entry generated.

       The predicate matches on the trailing path segment rather than on one assumed spelling of
       @Source. That matters: depending on heat version and options, the harvested Source can come
       out as "$(var.PublishDir)\SimpleDeFence.exe", as "SourceDir\SimpleDeFence.exe", or as an
       absolute path. An exact-string match against any single one of those shapes silently
       no-ops against the others - and a no-op here is not a benign failure, it reinstates the
       duplicate-file ICE30 collision this file exists to prevent.

       translate() normalises any forward slashes to backslashes before the suffix comparison
       (it is length-preserving, so string-length(@Source) is still the right basis). The second
       clause covers a bare, directory-less Source. Requiring the separator is what keeps this
       from over-matching: "SimpleDeFence.exe.config" and a hypothetical "XSimpleDeFence.exe"
       both fail the test. SimpleDeFence.exe is in any case the only name across all 293 files in
       the real publish tree that contains that substring at all. -->
  <xsl:key name="MainExeComponents"
           match="wix:Component[wix:File[
                    substring(translate(@Source, '/', '\'), string-length(@Source) - 17) = '\SimpleDeFence.exe'
                    or @Source = 'SimpleDeFence.exe']]"
           use="@Id" />

  <!-- Drop the component itself... -->
  <xsl:template match="wix:Component[key('MainExeComponents', @Id)]" />

  <!-- ...and the ComponentRef inside ComponentGroup PublishedDependencies that points at it,
       which would otherwise be an unresolved reference. -->
  <xsl:template match="wix:ComponentRef[key('MainExeComponents', @Id)]" />

</xsl:stylesheet>

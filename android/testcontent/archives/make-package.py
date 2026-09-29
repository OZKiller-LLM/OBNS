# Builds PackageRoute.zip: a minimal OpenBVE package - a package.xml in the format upstream's
# Manipulation.ReadPackage deserialises, and one route file under Route/.
# Usage: python make-package.py [output folder]
import os, sys, zipfile

out = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.abspath(__file__))
xml = '''<?xml version="1.0" encoding="utf-8"?>
<openBVE xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <Package>
    <PackageVersion>1.2.0</PackageVersion>
    <Name>Package Test Route</Name>
    <Author>OpenBVE Android port</Author>
    <Website>https://example.invalid</Website>
    <GUID>8D4B7E0A-3C1F-4B7E-9A51-2F6C0D9E1A77</GUID>
    <PackageType>Route</PackageType>
    <Description>A tiny route packaged with package.xml, to test the Windows install procedure.</Description>
    <Dependancies />
    <Reccomendations />
    <DependantPackages />
  </Package>
</openBVE>'''
route = 'With Route\n.Comment Package test route\nWith Train\n.Folder AndroidTest\nWith Track\n0,.Sta Test;;;;;;;;\n25,.Stop\n1000,\n'
with zipfile.ZipFile(os.path.join(out, 'PackageRoute.zip'), 'w', zipfile.ZIP_DEFLATED) as z:
    z.writestr('Package.xml', xml)
    z.writestr('Route/PackageTest/PackageTest.csv', route)
print('ok')

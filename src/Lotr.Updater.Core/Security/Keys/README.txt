Production release key(s) of the LotR MP Event updater.

Put the server's public key here as  release-signing.pub.pem  (the file `lotr-publish init-key --root <root>`
writes to <root>\data\secrets\release-signing.pub.pem; ONLY the .pub.pem, never the private .key).
Every *.pub.pem in this folder is embedded in the updater and trusted (several = key rotation).

A Release build of src/Lotr.Updater FAILS while this folder has no *.pub.pem, unless it is built with
-p:AllowNoProductionKey=true (such an exe refuses every release). See docs/MANIFEST.md, "Production key".

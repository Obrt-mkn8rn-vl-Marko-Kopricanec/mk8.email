/ip firewall nat
remove [find where comment="mk8 preflight TCP web"]
remove [find where comment="mk8 public TCP services"]
remove [find where comment~"^mk8 .* UDP services$"]
add chain=dstnat action=dst-nat in-interface-list=WAN dst-address=@@MK8_PUBLIC_IPV4@@ protocol=tcp dst-port=25,80,443,465,587,993 to-addresses=@@MK8_SERVER_IPV4@@ comment="mk8 public TCP services" place-before=0

/ip firewall filter
remove [find where comment="allow mk8 preflight TCP web"]
remove [find where comment="allow mk8 public TCP services"]
remove [find where comment~"^allow mk8 .* UDP services$"]
remove [find where comment="allow mk8 outbound SMTP"]
add chain=forward action=accept in-interface-list=WAN connection-nat-state=dstnat connection-state=new protocol=tcp dst-address=@@MK8_SERVER_IPV4@@ dst-port=25,80,443,465,587,993 comment="allow mk8 public TCP services" place-before=0
add chain=forward action=accept out-interface-list=WAN connection-state=new protocol=tcp src-address=@@MK8_SERVER_IPV4@@ dst-port=25 comment="allow mk8 outbound SMTP" place-before=0

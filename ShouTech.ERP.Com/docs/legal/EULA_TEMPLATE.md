# ShouTech ERP — End-User License Agreement (TEMPLATE)

> **Template status:** professional drafting template for the
> commercial phase, modelled on how comparable commercial ERP
> software frames grant, restrictions and liability. It is NOT
> legal advice. The shipped EULA must be the text reviewed and
> approved by the owner's lawyer in the owner's jurisdiction
> (owner step, recorded in docs/REDUCE_PLAN.md). Bracketed
> items are filled at issuance. The Arabic text is the
> co-official version for Arabic-speaking customers; in case of
> conflict the [governing version — lawyer to confirm] prevails.

---

## END-USER LICENSE AGREEMENT — ShouTech ERP

This End-User License Agreement ("Agreement") is a legal
agreement between you (the "Licensee", the customer named in
the License Certificate) and [VENDOR LEGAL NAME] ("Licensor")
for the ShouTech ERP software product (the "Software"),
including its modules, documentation, help content and design
files.

By installing, copying, activating or using the Software, the
Licensee agrees to be bound by this Agreement.

### 1. License grant
Subject to payment of the license fees and the terms of this
Agreement, the Licensor grants the Licensee a non-exclusive,
non-transferable license to install and use the Software for
its internal business operations, limited to: the modules
listed in the License Certificate, the number of seats /
terminals stated there, and the license term stated there.

### 2. License Certificate; activation
Each license is issued with a License Certificate recording
the customer name, licensed modules, seats, term and license
identifier. The license is bound to the Licensee's
installation by the activation mechanism described in the
product documentation; use beyond the certified modules,
seats or term is not licensed.

### 3. Ownership
The Software is licensed, not sold. The Licensor (and its
licensors, if any) retains all right, title and interest in
and to the Software, including all intellectual-property
rights. The Licensee's data — its companies, documents and
records created with the Software — belongs to the Licensee.

### 4. Restrictions
The Licensee shall not: (a) rent, lease, sublicense,
redistribute or provide the Software to third parties as a
service, except as expressly permitted in the License
Certificate; (b) reverse-engineer, decompile or disassemble
the Software, except to the extent such restriction is
prohibited by applicable law; (c) circumvent or disable the
license-activation, module-entitlement or integrity
mechanisms; (d) remove or alter copyright, trademark or
ownership notices in the Software, its outputs or its
documentation.

### 5. Copies; backup
The Licensee may make copies of the Software solely for
backup and archival purposes, and may copy its databases for
backup under the product's own backup facilities. Backup
copies remain subject to this Agreement.

### 6. Updates and support
Updates, upgrades and support are provided only under a
current support arrangement, on the terms offered at the
time. The Licensor may require installation of updates that
affect licensing or security mechanisms.

### 7. Data and privacy
The Software operates on the Licensee's machines, and the
Licensee's data remains the Licensee's property. The
compliance reporting, backup upload and license enforcement
described in Section 7A are conditions of this license and
form part of the product documentation for this Section.

### 7A. Compliance reporting, backup upload and license enforcement

> **[PENDING LAWYER REVIEW — adopted from the telemetry design
> study (docs/TELEMETRY_BACKUP_STUDY.md §9) on 2026-11-24. The
> enforcement mechanism described here must not ship before
> this clause, or its lawyer-approved successor, ships with
> the license. The encryption-key bracket in (c) is resolved
> per owner decision (C): the per-installation key is held by
> the Licensee; the Licensor stores ciphertext it cannot
> read.]**

(a) *Reporting.* As a condition of this license, the Software, when
the Licensee's machine is connected to the internet, transmits to
the Licensor's server, on launch or once daily: (i) information
about the Software installation (version, instance and device
identifiers, license key or dongle serial, license status, term and
licensed limits); (ii) information about the licensed company and
its users as recorded in the Software (company identifiers, legal
names, tax and commercial registration numbers, contact details;
user names, user identifiers, roles and active status); and (iii) a
device fingerprint consisting of one-way hashes of hardware and
operating-system components. Raw hardware serial numbers and user
passwords or password hashes are never transmitted.

(b) *Purpose.* Transmitted information is used solely to compare
the installation against the Licensor's licensing records, to
verify license compliance, and to provide the backup custody in
clause (c).

(c) *Backup upload.* Once daily, when connected, the Software
uploads one encrypted backup copy of the licensed company's data
(produced by the Software's own backup facility) to the Licensor's
server. The Licensor retains at most three (3) such copies per
company and deletes older copies. Backup copies are stored
encrypted; the encryption key is held by the Licensee and the
Licensor does not access the content of backup copies except as
required to provide restore assistance at the Licensee's request.

(d) *Enforcement.* If the comparison under clause (b) confirms that
an installation does not correspond to the Licensor's licensing
records, or the license is revoked for breach, the Licensor may
cause that copy of the Software to convert, immediately upon the
check that confirms the mismatch and without prior notice, to a
view-only demonstration state in which data can be viewed, printed
and exported but not created, altered, posted or deleted.
Conversion does not delete, alter or withhold the Licensee's data,
which remains the Licensee's property and remains fully exportable
in that state. A converted copy is restored to full function upon
the Licensor's clearance or re-registration of the installation.

(e) *Offline use.* The Software does not require an internet
connection to operate. Reporting and upload occur only when a
connection is available, and the absence of a connection does not
by itself affect the Licensee's use of the Software within the
license term.

### 8. Limited warranty
The Licensor warrants that the Software will substantially
conform to its documentation for [90] days from delivery.
Except as stated here, and to the maximum extent permitted by
law, the Software is provided "as is" without other
warranties, express or implied, including merchantability or
fitness for a particular purpose.

### 9. Limitation of liability
To the maximum extent permitted by law, the Licensor's total
liability under this Agreement is limited to the license fees
paid for the Software in the [12] months preceding the claim,
and the Licensor is not liable for indirect, incidental or
consequential damages, including lost profits or lost data,
except where such exclusion is prohibited by law.

### 10. Term and termination
This Agreement is effective from installation until the end
of the license term in the License Certificate (perpetual
licenses: until terminated). It terminates automatically if
the Licensee breaches Sections 1, 2 or 4. On termination the
Licensee must stop using and uninstall the Software; the
Licensee's data remains the Licensee's property and the
Software's read/export facilities for that data are governed
by the licensing policy in force (read access to one's own
data is never revoked by a licensing failure).

### 11. Governing law
This Agreement is governed by the laws of [JURISDICTION], and
disputes are subject to the courts of [JURISDICTION], unless
applicable law requires otherwise.

### 12. Entire agreement
This Agreement, together with the License Certificate, is
the entire agreement for the Software and supersedes prior
understandings about it.

---

## اتفاقية ترخيص المستخدم النهائي — شوتك ERP (ملخص العربية)

هذه الاتفاقية اتفاق قانوني بين المرخَّص له (العميل المسمّى في
شهادة الترخيص) و[الاسم القانوني للبائع] بشأن برنامج ShouTech
ERP. باستخدام البرنامج أو تثبيته أو تفعيله يوافق المرخَّص له
على بنودها. تشمل البنود الأساسية: منح ترخيص غير حصري وغير
قابل للتحويل مقيداً بالوحدات وعدد المقاعد والمدة الواردة في
شهادة الترخيص؛ بقاء ملكية البرنامج وحقوقه الفكرية للمرخِّص
وملكية بيانات العميل للعميل؛ حظر التأجير وإعادة التوزيع
والهندسة العكسية وتعطيل آليات التفعيل أو إزالة إشعارات
الملكية؛ حدود الضمان والمسؤولية كما في النص الإنجليزي؛
والإنهاء عند الإخلال بالبنود الجوهرية مع بقاء حق العميل في
بياناته. [النص الحاكم عند التعارض والاختصاص القضائي: يُستكمل
بعد مراجعة المحامي.]

كما تشترط الاتفاقية أن يرسل البرنامج عند توفر الاتصال بالإنترنت،
عند التشغيل أو مرة يومياً، معلومات عامة عن البرنامج والجهاز
(النسخة ومعرّفات التثبيت والترخيص وبصمة جهاز مكوّنة من تجزئات
أحادية الاتجاه لمكوّنات العتاد، دون الأرقام التسلسلية الخام ودون
أي كلمات مرور أو تجزئاتها) ومعلومات الشركة المرخّصة ومستخدميها،
وذلك لمقارنتها بسجلات الترخيص لدى المرخِّص والتحقق من الالتزام؛
وأن يرفع نسخة احتياطية مشفّرة واحدة يومياً من بيانات الشركة
يحتفظ المرخِّص بثلاث نسخ منها كحد أقصى. وإذا أكّدت المقارنة عدم
تطابق التثبيت مع سجلات الترخيص جاز للمرخِّص تحويل تلك النسخة
فوراً ودون إشعار مسبق إلى وضع العرض التجريبي (اطّلاع وطباعة
وتصدير فقط، دون إنشاء أو تعديل أو ترحيل)، دون أن يمسّ ذلك بيانات
المرخَّص له التي تبقى ملكاً له وقابلة للتصدير كاملة، وتعود النسخة
إلى كامل وظائفها عند تسوية الوضع أو إعادة التسجيل. ولا يتطلب
البرنامج اتصالاً بالإنترنت للعمل.

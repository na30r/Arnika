export const locales = ["en", "fa"] as const;
export type Locale = (typeof locales)[number];
export const defaultLocale: Locale = "en";

type Dict = Record<string, { en: string; fa: string }>;

const strings: Dict = {
  "nav.home": { en: "Home", fa: "خانه" },
  "nav.signIn": { en: "Sign in", fa: "ورود" },
  "nav.signUp": { en: "Sign up", fa: "ثبت‌نام" },
  "nav.signOut": { en: "Sign out", fa: "خروج" },
  "nav.profile": { en: "Profile", fa: "پروفایل" },
  "nav.lang": { en: "Language", fa: "زبان" },
  "home.title": { en: "Documentation mirror", fa: "آینه اسناد" },
  "home.subtitle": {
    en: "Crawl a documentation page and optional first-level links, then preview locally.",
    fa: "یک صفحه مستندات و در صورت نیاز لینک‌های سطح اول را بگیرد و به‌صورت محلی نمایش دهید."
  },
  "form.url": { en: "Documentation page URL", fa: "آدرس URL صفحه مستندات" },
  "form.version": { en: "Version", fa: "نسخه" },
  "form.mirror": { en: "Mirror page", fa: "آینه کردن" },
  "form.mirroring": { en: "Mirroring…", fa: "در حال آینه…" },
  "form.linkDrill": { en: "Link drill count (non-recursive queue)", fa: "تعداد حفاری لینک" },
  "form.linkDrillHint": {
    en: "Only first-level links from the start page are queued.",
    fa: "فقط لینک‌های سطح اول صفحه شروع وارد صف می‌شوند."
  },
  "form.languages": { en: "Languages (comma-separated)", fa: "زبان‌ها (با کاما)" },
  "form.doNotTranslate": { en: "Do not translate (one per line)", fa: "ترجمه نشود (یکی در هر خط)" },
  "form.previewLanguage": { en: "Preview language", fa: "زبان پیش‌نمایش" },
  "form.openExisting": { en: "Open existing mirrored page", fa: "باز کردن صفحه آینه شده" },
  "preview.openTab": { en: "Open in new tab", fa: "باز در تب جدید" },
  "preview.heading": { en: "Mirrored page preview", fa: "پیش‌نمایش صفحه آینه" },
  "auth.loginTitle": { en: "Sign in", fa: "ورود" },
  "auth.registerTitle": { en: "Create account", fa: "ساخت حساب" },
  "auth.username": { en: "Username", fa: "نام کاربری" },
  "auth.phone": { en: "Phone (optional)", fa: "تلفن (اختیاری)" },
  "auth.password": { en: "Password", fa: "رمز عبور" },
  "auth.submitLogin": { en: "Sign in", fa: "ورود" },
  "auth.submitRegister": { en: "Create account", fa: "ثبت‌نام" },
  "auth.needAccount": { en: "No account? Sign up", fa: "حساب ندارید؟ ثبت‌نام" },
  "auth.haveAccount": { en: "Have an account? Sign in", fa: "حساب دارید؟ ورود" },
  "auth.signingIn": { en: "Please wait…", fa: "لطفا صبر کنید…" },
  "profile.title": { en: "Profile", fa: "پروفایل" },
  "profile.subscription": { en: "Subscription ends", fa: "پایان اشتراک" },
  "profile.noSubscription": { en: "Not set", fa: "تعریف نشده" },
  "profile.active": { en: "Active", fa: "فعال" },
  "profile.expired": { en: "Expired", fa: "منقضی" },
  "profile.history": { en: "Mirror history", fa: "تاریخچه آینه" },
  "profile.empty": { en: "No saved crawls yet.", fa: "هنوز رکوردی نیست." },
  "error.signIn": { en: "Sign in required to mirror. Use Sign in in the bar above.", fa: "برای آینه باید وارد شوید." }
};

export function t(key: string, locale: Locale): string {
  const entry = strings[key];
  if (!entry) {
    return key;
  }
  return locale === "fa" ? entry.fa : entry.en;
}

export function isRtl(locale: Locale): boolean {
  return locale === "fa";
}

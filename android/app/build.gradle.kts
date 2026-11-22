plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "org.tetherbridge.classicapp"
    compileSdk = 35

    defaultConfig {
        applicationId = "org.tetherbridge.classicapp"
        minSdk = 26
        targetSdk = 35
        versionCode = 67
        versionName = "0.6.7"
    }

    buildFeatures {
        buildConfig = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

kotlin {
    jvmToolchain(17)
}

dependencies {
    // QR pairing only. No original app code/assets/protocols are used.
    implementation("com.google.zxing:core:3.5.4")
    testImplementation("junit:junit:4.13.2")
}

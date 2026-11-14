package org.libationmobile.app;

import android.content.Context;
import android.net.Uri;
import android.os.Handler;
import android.os.Looper;

import androidx.media3.common.MediaItem;
import androidx.media3.common.MimeTypes;
import androidx.media3.transformer.Composition;
import androidx.media3.transformer.ExportException;
import androidx.media3.transformer.ExportResult;
import androidx.media3.transformer.Transformer;

import java.io.File;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

/**
 * Converts a local, unprotected input into AAC in an MP4-family container.
 * The caller must reject protected Audible containers before invoking this bridge.
 */
public final class UnprotectedM4bExporter {
    private UnprotectedM4bExporter() { }

    /**
     * Blocking bridge intended to be called from a worker thread. Transformer
     * itself is created and started on the Android main looper because Media3
     * requires a single application thread for all Transformer access.
     *
     * @return null on success, otherwise a short safe error message.
     */
    public static String export(Context context, String inputPath, String outputPath) {
        if (context == null || inputPath == null || outputPath == null) {
            return "The M4B export request was incomplete.";
        }

        CountDownLatch finished = new CountDownLatch(1);
        AtomicReference<String> failure = new AtomicReference<>(null);
        Handler main = new Handler(Looper.getMainLooper());

        Runnable start = () -> {
            try {
                Transformer.Listener listener = new Transformer.Listener() {
                    @Override
                    public void onCompleted(Composition composition, ExportResult result) {
                        finished.countDown();
                    }

                    @Override
                    public void onError(
                            Composition composition,
                            ExportResult result,
                            ExportException exception) {
                        failure.compareAndSet(null,
                                "Android could not convert this unprotected audio file to M4B.");
                        finished.countDown();
                    }
                };

                Transformer transformer = new Transformer.Builder(context.getApplicationContext())
                        .setAudioMimeType(MimeTypes.AUDIO_AAC)
                        .addListener(listener)
                        .build();

                MediaItem item = MediaItem.fromUri(Uri.fromFile(new File(inputPath)));
                transformer.start(item, outputPath);
            } catch (Exception exception) {
                failure.compareAndSet(null,
                        "Android could not start the unprotected M4B export.");
                finished.countDown();
            }
        };

        if (!main.post(start)) {
            return "Android's media thread is unavailable.";
        }

        try {
            if (!finished.await(4, TimeUnit.HOURS)) {
                return "The M4B export did not finish before the safety timeout.";
            }
        } catch (InterruptedException exception) {
            Thread.currentThread().interrupt();
            return "The M4B export was interrupted.";
        }

        return failure.get();
    }
}
